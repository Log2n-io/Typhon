using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Reflection;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

/// <summary>
/// The exact-schema open (#1485, <see cref="SchemaMatch.Exact"/>) and the assembly manifest's identity: a database opened by a drill or a restore's settle
/// opens only with the build that wrote it, and the manifest names that build (the identity of every assembly declaring a stored component or archetype, as
/// the last ordinary open registered it).
/// </summary>
public partial class DatabaseEngine
{
    /// <summary>The manifest rows this session brought up to the identity of the assembly it loaded (each once).</summary>
    private HashSet<ushort> _assemblyIdentityChecked;

    /// <summary>Test seam: invoked by <see cref="InitializeArchetypes"/> once the catalog's archetype rows are loaded. Null in production.</summary>
    internal Action AfterPersistedArchetypesLoadedForTest { get; set; }

    /// <summary>Test seam: an exact open that skips its checks — the SCHEMA-14 mutant. False in production.</summary>
    internal bool ExactSchemaChecksOffForTest { get; set; }

    /// <summary>Whether this open refuses any difference from the database's schema (<see cref="DatabaseEngineOptions.SchemaMatch"/>).</summary>
    internal bool ExactSchema => _options.SchemaMatch == SchemaMatch.Exact && !ExactSchemaChecksOffForTest;

    /// <summary>
    /// An exact open's check of a component the catalog holds, before anything of it is written: its revision, storage mode, size, fields and indexes,
    /// its name, and the identity of the assembly declaring it must be the catalog's. Throws <see cref="SchemaMismatchException"/> listing every difference.
    /// </summary>
    private void ThrowIfComponentNotExact(string schemaName, string persistedKey, in ComponentR1 persisted, FieldR1[] persistedFields,
        FieldIdResolver resolver, DBComponentDefinition definition)
    {
        var differences = new List<string>();
        if (persistedKey != schemaName)
        {
            differences.Add($"the database holds it as '{persistedKey}' (a rename to carry forward)");
        }

        if (persisted.SchemaRevision != definition.Revision)
        {
            differences.Add($"revision {persisted.SchemaRevision} in the database, {definition.Revision} in this build");
        }

        if ((StorageMode)persisted.StorageMode != definition.StorageMode)
        {
            differences.Add($"stored as {(StorageMode)persisted.StorageMode}, declared {definition.StorageMode}");
        }

        if (persisted.CompSize != definition.ComponentStorageSize)
        {
            differences.Add($"{persisted.CompSize} bytes per instance in the database, {definition.ComponentStorageSize} in this build");
        }

        if (persistedFields == null)
        {
            differences.Add("the database holds no field catalog for it");
        }
        else
        {
            CompareFields(persistedFields, definition, differences);
            if (resolver is { HasChanges: true } && differences.Count == 0)
            {
                differences.Add("its field ids differ from the catalog's");
            }
        }

        if (AssemblyMismatch(definition.POCOType.Assembly, persisted.AssemblyId) is { } assembly)
        {
            differences.Add(assembly);
        }

        if (differences.Count > 0)
        {
            throw new SchemaMismatchException($"Component '{schemaName}'", differences);
        }
    }

    /// <summary>
    /// Every field the catalog records against the build's, by field id: its name, type, element type, array length, static flag, index flags, realm key,
    /// offset and size — all <see cref="FieldR1"/> holds — and no field on one side only. Stricter than <see cref="SchemaValidator.ComputeDiff"/>, which
    /// grades changes for a migration: here nothing is graded, anything differs.
    /// </summary>
    private static void CompareFields(FieldR1[] persistedFields, DBComponentDefinition definition, List<string> differences)
    {
        var byId = new Dictionary<int, FieldR1>(persistedFields.Length);
        foreach (var f in persistedFields)
        {
            byId[f.FieldId] = f;
        }

        var seen = new HashSet<int>();
        foreach (var field in definition.FieldsByName.Values)
        {
            seen.Add(field.FieldId);
            if (!byId.TryGetValue(field.FieldId, out var p))
            {
                differences.Add($"field '{field.Name}': not in the database (this build adds it)");
                continue;
            }

            var what = new List<string>();
            if (p.Name.AsString != field.Name)
            {
                what.Add($"named '{p.Name.AsString}' in the database");
            }

            if (p.Type != field.Type || p.UnderlyingType != field.UnderlyingType || p.ArrayLength != field.ArrayLength)
            {
                what.Add($"type {p.Type}/{p.UnderlyingType}[{p.ArrayLength}] in the database, {field.Type}/{field.UnderlyingType}[{field.ArrayLength}] here");
            }

            if (p.OffsetInComponentStorage != field.OffsetInComponentStorage || p.SizeInComponentStorage != field.SizeInComponentStorage)
            {
                what.Add($"at {p.OffsetInComponentStorage}+{p.SizeInComponentStorage} in the database, {field.OffsetInComponentStorage}+"
                         + $"{field.SizeInComponentStorage} here");
            }

            if (p.IsStatic != field.IsStatic || p.IsRealmKey != field.IsRealmKey)
            {
                what.Add("its static or realm-key flag differs");
            }

            if (p.HasIndex != field.HasIndex || p.IndexAllowMultiple != field.IndexAllowMultiple || p.IndexSparse != field.IndexSparse)
            {
                what.Add("its index differs");
            }

            if (what.Count > 0)
            {
                differences.Add($"field '{field.Name}': {string.Join(", ", what)}");
            }
        }

        foreach (var p in persistedFields)
        {
            if (!seen.Contains(p.FieldId))
            {
                differences.Add($"field '{p.Name.AsString}': not in this build (the database holds it)");
            }
        }
    }

    /// <summary>
    /// An exact open's check of the archetypes, in <see cref="InitializeArchetypes"/> after the passes that decide what the open consumes and before any of
    /// it is built, written, forked or replayed. The open classifies under the inspector's policy (<see cref="ArchetypeSetMigrationPolicy.Refuse"/>), so
    /// every archetype it would migrate, rewrite or refuse — a set change, a revision bump, stale stored names, a per-archetype refusal, and one skipped for
    /// a component the build declares but the database never stored — is in <see cref="RefusedArchetypeSetChanges"/>: each is a difference. Each archetype
    /// it keeps (<paramref name="resolved"/>: of its own load context, its slots resolved, one per durable name) must be the catalog's row, under its own
    /// name, declared by the assembly the manifest names.
    /// </summary>
    private void ThrowIfArchetypesNotExact(Dictionary<ushort, ComponentTable[]> resolved)
    {
        var differences = new List<string>();
        foreach (var change in RefusedArchetypeSetChanges)
        {
            differences.Add($"archetype '{change.ArchetypeName}': {change.Kind}, revision {change.FromRevision} in the database, {change.ToRevision} in "
                            + $"this build{(string.IsNullOrEmpty(change.Changes) ? "" : $" ({change.Changes})")}");
        }

        foreach (var archetypeId in resolved.Keys)
        {
            var meta = ArchetypeRegistry.GetMetadata(archetypeId);
            if (!TryGetPersistedArchetype(meta, out var persisted))
            {
                differences.Add($"archetype '{meta.Name}': not in the database (this build adds it)");
                continue;
            }

            if (!_persistedArchetypes.ContainsKey(meta.Name))
            {
                differences.Add($"archetype '{meta.Name}': the database holds it as '{persisted.Arch.Name.AsString}' (a rename to carry forward)");
            }

            if (AssemblyMismatch(meta.ArchetypeType?.Assembly, persisted.Arch.AssemblyId) is { } assembly)
            {
                differences.Add($"archetype '{meta.Name}': {assembly}");
            }
        }

        if (differences.Count > 0)
        {
            throw new SchemaMismatchException("The archetypes", differences);
        }
    }

    /// <summary>
    /// Why <paramref name="asm"/> is not the assembly the manifest names for row <paramref name="assemblyId"/>: a different name, version or public-key
    /// token, or no row at all; <c>null</c> when it is (or for the core engine assembly, which is never in the manifest).
    /// </summary>
    internal string AssemblyMismatch(Assembly asm, ushort assemblyId)
    {
        if (asm == null || asm == typeof(DatabaseEngine).Assembly)
        {
            return null;
        }

        var name = asm.GetName();
        if (assemblyId == 0 || _persistedAssemblies == null || !_persistedAssemblies.TryGetValue(assemblyId, out var row))
        {
            return $"the database's assembly manifest names no assembly for it; this build's is {Describe(name)}";
        }

        return SameIdentity(row.Asm, name) ? null : $"the database was written by {Describe(row.Asm)}, this build is {Describe(name)}";
    }

    /// <summary>
    /// What an ordinary open's registrations found of the assemblies declaring them, applied by <see cref="ApplyPendingDeclaringAssemblies"/> once the open
    /// completed: a refused open (a downgrade, a breaking change, an archetype refusal) leaves the manifest and the timeline as they were.
    /// </summary>
    private List<(Assembly Asm, ushort AssemblyId, Action<ushort> Repoint)> _pendingDeclaringAssemblies;

    /// <summary>
    /// Notes that <paramref name="asm"/> declares a catalog row naming manifest row <paramref name="assemblyId"/>; <paramref name="repoint"/> points the row
    /// at another manifest row when the type moved. Applied at once after the open completed (a runtime registration), else at its end.
    /// </summary>
    private void NoteDeclaringAssembly(Assembly asm, ushort assemblyId, Action<ushort> repoint)
    {
        if (asm == null || asm == typeof(DatabaseEngine).Assembly || ExactSchema)
        {
            return;
        }

        (_pendingDeclaringAssemblies ??= []).Add((asm, assemblyId, repoint));
        if (_openCompleted)
        {
            ApplyPendingDeclaringAssemblies();
        }
    }

    /// <summary>
    /// Brings the manifest to the build this open registered (#1485): each noted row takes the loaded identity, each moved type's catalog row names its new
    /// assembly, and a manifest row no catalog row names any more is retired (blanked: every reader skips it), so no drill is asked for an assembly the
    /// database no longer needs. After a completed <see cref="InitializeArchetypes"/>, before the open is published.
    /// </summary>
    private void ApplyPendingDeclaringAssemblies()
    {
        var pending = _pendingDeclaringAssemblies;
        _pendingDeclaringAssemblies = null;
        if (pending == null)
        {
            return;
        }

        var repointed = false;
        foreach (var (asm, assemblyId, repoint) in pending)
        {
            var declaring = RecordDeclaringAssembly(asm, assemblyId);
            if (declaring != assemblyId)
            {
                repoint(declaring);
                repointed = true;
            }
        }

        if (repointed)
        {
            RetireUnreferencedAssemblies();
        }
    }

    /// <summary>Blanks every manifest row no catalog row names (after a type moved to another assembly).</summary>
    private void RetireUnreferencedAssemblies()
    {
        var named = new HashSet<ushort>();
        foreach (var (_, comp) in _persistedComponents.Values)
        {
            named.Add(comp.AssemblyId);
        }

        foreach (var (_, arch) in _persistedArchetypes?.Values ?? (IEnumerable<(int, ArchetypeR1)>)[])
        {
            named.Add(arch.AssemblyId);
        }

        List<ushort> retired = null;
        foreach (var (id, row) in _persistedAssemblies)
        {
            if (!named.Contains(id))
            {
                var blank = default(AssemblyR1);
                var cs = MMF.CreateChangeSet();
                SystemCrud.Update(_assembliesTable, row.ChunkId, ref blank, EpochManager, cs);
                cs.SaveChanges();
                _assemblyIdByName?.Remove(row.Asm.SimpleName.AsString);
                (retired ??= []).Add(id);
            }
        }

        foreach (var id in retired ?? [])
        {
            _persistedAssemblies.Remove(id);
        }
    }

    /// <summary>
    /// An ordinary open keeps the manifest naming the build that writes the database (#1485), for a catalog row naming manifest row
    /// <paramref name="assemblyId"/> whose type <paramref name="asm"/> declares: the row of that assembly takes the loaded one's identity (version,
    /// public-key token), and a type now declared by another assembly (moved, or a row from before the manifest) names that assembly's row, created when
    /// new. Either change forks the timeline first, as a schema change does: another build's records are reproduced only by that build, so no restore
    /// replays across it. Called once the open completed (<see cref="ApplyPendingDeclaringAssemblies"/>); an exact open never gets here — it refused the
    /// difference. Returns the manifest row the catalog row must name.
    /// </summary>
    private ushort RecordDeclaringAssembly(Assembly asm, ushort assemblyId)
    {
        if (asm == null || asm == typeof(DatabaseEngine).Assembly || _assembliesTable == null || _persistedAssemblies == null)
        {
            return assemblyId;
        }

        var name = asm.GetName();
        if (assemblyId != 0 && _persistedAssemblies.TryGetValue(assemblyId, out var row) && SameName(row.Asm, name))
        {
            RecordAssemblyIdentity(asm, assemblyId, row);
            return assemblyId;
        }

        ForkTimeline(TimelineReason.BuildChange);
        var cs = MMF.CreateChangeSet();
        var id = GetOrCreateAssemblyId(asm, cs);
        cs.SaveChanges();
        if (_persistedAssemblies.TryGetValue(id, out var declaring))
        {
            RecordAssemblyIdentity(asm, id, declaring);
        }

        return id;
    }

    /// <summary>The identity half of <see cref="RecordDeclaringAssembly"/>: the row <paramref name="assemblyId"/> takes the loaded build's.</summary>
    private void RecordAssemblyIdentity(Assembly asm, ushort assemblyId, (int ChunkId, AssemblyR1 Asm) row)
    {
        var name = asm.GetName();
        if (!(_assemblyIdentityChecked ??= []).Add(assemblyId) || SameIdentity(row.Asm, name))
        {
            return;   // once per session; the same build
        }

        ForkTimeline(TimelineReason.BuildChange);
        var was = Describe(row.Asm);
        var updated = row.Asm;
        var v = name.Version ?? new Version(0, 0, 0, 0);
        updated.VerMajor = v.Major;
        updated.VerMinor = v.Minor;
        updated.VerBuild = v.Build < 0 ? 0 : v.Build;
        updated.VerRevision = v.Revision < 0 ? 0 : v.Revision;
        updated.PublicKeyToken = TokenToULong(name.GetPublicKeyToken());
        var cs = MMF.CreateChangeSet();
        SystemCrud.Update(_assembliesTable, row.ChunkId, ref updated, EpochManager, cs);
        cs.SaveChanges();
        _persistedAssemblies[assemblyId] = (row.ChunkId, updated);
        LogAssemblyIdentityRecorded(name.Name, was, Describe(updated));
    }

    /// <summary>A component's catalog row re-pointed at manifest row <paramref name="assemblyId"/> (its type moved to another assembly), durably.</summary>
    private void RepointComponentAssembly(string persistedKey, int chunkId, ushort assemblyId)
    {
        var cs = MMF.CreateChangeSet();
        SystemCrud.Read(_componentsTable, chunkId, out ComponentR1 comp, EpochManager);
        comp.AssemblyId = assemblyId;
        SystemCrud.Update(_componentsTable, chunkId, ref comp, EpochManager, cs);
        cs.SaveChanges();
        if (_persistedComponents.TryGetValue(persistedKey, out var cached))
        {
            cached.Comp.AssemblyId = assemblyId;
            _persistedComponents[persistedKey] = cached;
        }
    }

    /// <summary>An archetype's catalog row re-pointed at manifest row <paramref name="assemblyId"/>, durably; returns the row as now cached.</summary>
    private (int ChunkId, ArchetypeR1 Arch) RepointArchetypeAssembly(ArchetypeMetadata meta, (int ChunkId, ArchetypeR1 Arch) persisted, ushort assemblyId)
    {
        var arch = persisted.Arch;
        arch.AssemblyId = assemblyId;
        var cs = MMF.CreateChangeSet();
        SystemCrud.Update(GetComponentTable<ArchetypeR1>(), persisted.ChunkId, ref arch, EpochManager, cs);
        cs.SaveChanges();
        var key = _persistedArchetypes.ContainsKey(meta.Name) ? meta.Name : meta.PreviousName ?? meta.Name;
        _persistedArchetypes[key] = (persisted.ChunkId, arch);
        return (persisted.ChunkId, arch);
    }

    /// <summary>
    /// The schema assemblies declaring the components and archetypes this engine registered, by name (the core engine's excluded): what a drill's open
    /// ran with.
    /// </summary>
    internal List<BackupSchemaAssembly> SchemaAssemblies()
    {
        var seen = new HashSet<Assembly>();
        var result = new List<BackupSchemaAssembly>();
        foreach (var type in (Type[])[.. _registeredComponentTypes, .. _registeredArchetypeTypes])
        {
            var asm = type.Assembly;
            if (asm == typeof(DatabaseEngine).Assembly || !seen.Add(asm))
            {
                continue;
            }

            var name = asm.GetName();
            var v = name.Version ?? new Version(0, 0, 0, 0);
            var token = TokenToULong(name.GetPublicKeyToken());
            result.Add(new BackupSchemaAssembly(name.Name, $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}.{Math.Max(v.Revision, 0)}",
                token == 0 ? null : Convert.ToHexString(ULongToToken(token)).ToLowerInvariant(), asm.ManifestModule.ModuleVersionId,
                string.IsNullOrEmpty(asm.Location) ? null : asm.Location));
        }

        result.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return result;
    }

    /// <summary>Whether manifest row <paramref name="row"/> names <paramref name="name"/>'s assembly, as a 63-byte <see cref="String64"/> holds it.</summary>
    private static bool SameName(in AssemblyR1 row, AssemblyName name) =>
        string.Equals(row.SimpleName.AsString, ((String64)(name.Name ?? "")).AsString, StringComparison.OrdinalIgnoreCase);

    private static bool SameIdentity(in AssemblyR1 row, AssemblyName name)
    {
        var v = name.Version ?? new Version(0, 0, 0, 0);
        return SameName(row, name)
               && row.VerMajor == v.Major && row.VerMinor == v.Minor && row.VerBuild == Math.Max(v.Build, 0) && row.VerRevision == Math.Max(v.Revision, 0)
               && row.PublicKeyToken == TokenToULong(name.GetPublicKeyToken());
    }

    private static string Describe(in AssemblyR1 row) =>
        $"{row.SimpleName.AsString} {row.VerMajor}.{row.VerMinor}.{row.VerBuild}.{row.VerRevision} ({Token(row.PublicKeyToken)})";

    private static string Describe(AssemblyName name)
    {
        var v = name.Version ?? new Version(0, 0, 0, 0);
        return $"{name.Name} {v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}.{Math.Max(v.Revision, 0)} ({Token(TokenToULong(name.GetPublicKeyToken()))})";
    }

    private static string Token(ulong token) => token == 0 ? "unsigned" : $"token {Convert.ToHexString(ULongToToken(token)).ToLowerInvariant()}";

    [LoggerMessage(LogLevel.Information, "Schema assembly {Name}: the manifest now names {Now} (was {Was}), the build this open registered")]
    private partial void LogAssemblyIdentityRecorded(string name, string was, string now);
}

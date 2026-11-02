using System.Reflection;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.Workbench.Schema;

/// <summary>
/// Attempts to register component types from a <see cref="LoadedSchema"/> against a running
/// <see cref="DatabaseEngine"/> and classifies the outcome as one of Ready / MigrationRequired /
/// Incompatible.
///
/// <para>Each component is registered independently — a failure on one (e.g., a test-only
/// <c>TransientBad</c> fixture with an unsupported attribute combination, or a schema name
/// collision) does NOT abort the rest. The engine remains usable for components that DO load
/// cleanly; the aggregate <see cref="State"/> still reflects the worst-case outcome so the UI
/// can surface a Migration/Incompatibility banner while still populating the Schema Inspector
/// with the components that did succeed.</para>
/// </summary>
public static class SchemaCompatibility
{
    public enum State
    {
        Ready,
        MigrationRequired,
        Incompatible,
    }

    public sealed record Diagnostic(string ComponentName, string Kind, string Detail);

    public sealed record Result(State State, Diagnostic[] Diagnostics, int RegisteredCount);

    /// <summary>
    /// Diagnostic kind for a component the loaded assembly declares but the database does not contain. Informational, NOT an
    /// error: it is the normal and correct outcome of pointing a general-purpose schema assembly at a database that uses part
    /// of it. It must never contribute to <see cref="State.MigrationRequired"/> or <see cref="State.Incompatible"/> — a
    /// database is not broken for lacking something it never had.
    /// </summary>
    public const string NotInDatabaseKind = "not_in_database";

    /// <summary>
    /// Diagnostic kind for an archetype whose stored component set (or revision) differs from the loaded assembly's (#1394). The Workbench opens with
    /// <see cref="ArchetypeSetMigrationPolicy.Refuse"/>, so the archetype is skipped and the file left as stored: migrating it would rewrite every entity
    /// of an archetype the user only meant to look at. Escalates <see cref="State.Ready"/> to <see cref="State.MigrationRequired"/>; the application,
    /// opening the file with its own build, migrates it.
    /// </summary>
    public const string ArchetypeSetChangeKind = "archetype_set_change";

    /// <summary>
    /// Diagnostic kind for an open the engine refused over an archetype's stored schema (<see cref="ArchetypeSchemaException"/>): a downgrade, a removed
    /// component, a component migrating under an archetype this process does not open (R10, typically an archetype class absent from the loaded
    /// assemblies)… The detail is the engine's message, which names the archetype and the fix.
    /// </summary>
    public const string ArchetypeSchemaRefusedKind = "archetype_schema_refused";

    /// <summary>Diagnostic kind for any other failure of <c>InitializeArchetypes</c>; the detail carries the exception.</summary>
    public const string ArchetypeInitFailedKind = "archetype_init_failed";

    /// <summary>
    /// Adds one <see cref="ArchetypeSetChangeKind"/> diagnostic per archetype the engine skipped under <see cref="ArchetypeSetMigrationPolicy.Refuse"/>
    /// (<see cref="DatabaseEngine.RefusedArchetypeSetChanges"/>, read after <c>InitializeArchetypes</c>) and escalates <see cref="State.Ready"/> to
    /// <see cref="State.MigrationRequired"/>. Errors stay ahead of the informational <see cref="NotInDatabaseKind"/> notes.
    /// </summary>
    public static Result WithArchetypeSetChanges(DatabaseEngine engine, Result result)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(result);

        var refused = engine.RefusedArchetypeSetChanges;
        if (refused.Count == 0)
        {
            return result;
        }

        var archetypes = new List<Diagnostic>(refused.Count);
        foreach (var r in refused)
        {
            if (r.Kind == Engine.ArchetypeSetChangeKind.Refused)
            {
                // Not a migration the application's build would run: an open consuming it is refused. Say that, with the engine's own refusal (it names
                // the remedy); the rest of the database is open.
                archetypes.Add(new Diagnostic(r.ArchetypeName, ArchetypeSetChangeKind,
                    $"Archetype '{r.ArchetypeName}' is not shown: the loaded schema's declaration of it is refused against the stored one. {r.Changes}"));
                continue;
            }

            var what = r.Kind switch
            {
                Engine.ArchetypeSetChangeKind.SetChange => $"its component set changed ({r.Changes})",
                Engine.ArchetypeSetChangeKind.RevisionOnly => "its revision changed with the same set",
                _ => "its stored component names predate a component rename",
            };
            archetypes.Add(new Diagnostic(r.ArchetypeName, ArchetypeSetChangeKind,
                $"Archetype '{r.ArchetypeName}' is stored at revision {r.FromRevision} and the loaded schema declares revision {r.ToRevision}: {what}. The "
                + "Workbench does not migrate archetype sets, so this archetype is not shown and the file is left as stored. Open the database with the "
                + "application's build to migrate it."));
        }

        var diagnostics = new List<Diagnostic>(result.Diagnostics.Length + archetypes.Count);
        foreach (var d in result.Diagnostics)
        {
            if (d.Kind != NotInDatabaseKind)
            {
                diagnostics.Add(d);
            }
        }

        diagnostics.AddRange(archetypes);
        foreach (var d in result.Diagnostics)
        {
            if (d.Kind == NotInDatabaseKind)
            {
                diagnostics.Add(d);
            }
        }

        var state = result.State == State.Ready ? State.MigrationRequired : result.State;
        return new Result(state, diagnostics.ToArray(), result.RegisteredCount);
    }

    /// <summary>
    /// Runs <c>InitializeArchetypes</c> on <paramref name="engine"/> and folds its outcome into <paramref name="result"/> — the Workbench open's one call
    /// (<c>EngineLifecycle</c>). Either way the archetypes the engine skipped under
    /// <see cref="ArchetypeSetMigrationPolicy.Refuse"/> are reported (<see cref="WithArchetypeSetChanges"/>): an open that throws has recorded them already,
    /// and dropping them hid the set changes the user has to act on behind the one refusal. A failure becomes the FIRST diagnostic, ahead of every other
    /// error and every informational note, since the banner shows the first three; <paramref name="failure"/> is the exception, null on success.
    /// </summary>
    public static Result InitializeArchetypes(DatabaseEngine engine, Result result, out Exception failure)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(result);

        failure = null;
        try
        {
            engine.InitializeArchetypes();
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        var withArchetypes = WithArchetypeSetChanges(engine, result);
        if (failure == null)
        {
            return withArchetypes;
        }

        var diagnostics = new Diagnostic[withArchetypes.Diagnostics.Length + 1];
        diagnostics[0] = DescribeInitializeArchetypesFailure(failure);
        withArchetypes.Diagnostics.CopyTo(diagnostics, 1);
        var state = withArchetypes.State == State.Ready ? State.MigrationRequired : withArchetypes.State;
        return new Result(state, diagnostics, withArchetypes.RegisteredCount);
    }

    /// <summary>
    /// The diagnostic for an <c>InitializeArchetypes</c> that threw. An <see cref="ArchetypeSchemaException"/> is a refusal the engine explains in full, so
    /// its message is the detail (no stack trace) under <see cref="ArchetypeSchemaRefusedKind"/>, named after the archetype; anything else is reported as
    /// <see cref="ArchetypeInitFailedKind"/> with the whole exception.
    /// </summary>
    public static Diagnostic DescribeInitializeArchetypesFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is ArchetypeSchemaException refusal
            ? new Diagnostic(refusal.ArchetypeName ?? "(InitializeArchetypes)", ArchetypeSchemaRefusedKind, refusal.Message)
            : new Diagnostic("(InitializeArchetypes)", ArchetypeInitFailedKind, exception.ToString());
    }

    public static Result ClassifyAndRegister(DatabaseEngine engine, LoadedSchema loaded)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(loaded);

        if (loaded.ComponentTypes.Length == 0)
        {
            return new Result(State.Ready, [], 0);
        }

        var diagnostics = new List<Diagnostic>();
        var registered = 0;
        var hadDowngrade = false;
        var hadMigrationFailed = false;
        var hadBreakingChange = false;
        var hadOther = false;

        // The DATABASE is the reference; the assembly is checked against it. Registering a component the database has never
        // held does not read it — it CREATES it (DatabaseEngine's "Create path"), allocating segments and persisting a
        // ComponentR1 row. That turned opening a database in an inspector into a permanent, silent schema mutation: the
        // Workbench loads the whole schema DLL, so every type the file lacked was written into it (measured: 20 components /
        // 6.2 MB on a database that declared 6). Those invented segments are also what the next application write destroys,
        // since it does not know they exist (CK-09). ADR-055's migrate-on-open is preserved exactly — it concerns components
        // that ARE present and behind — but a component absent from the file is reported, never invented.
        var persisted = engine.PersistedComponents;

        foreach (var type in loaded.ComponentTypes)
        {
            var attribute = type.GetCustomAttribute<ComponentAttribute>();
            var name = attribute?.Name ?? type.Name;

            // Match the engine's own reopen matching (DatabaseEngine.cs:2576): a renamed component is persisted under its
            // PREVIOUS name until the rename is carried forward, so keying on the current name alone would report a renamed
            // component as absent and silently defeat the [Component(PreviousName)] hatch (#514 D4).
            var previousName = attribute?.PreviousName;
            var isInDatabase = persisted != null
                && (persisted.ContainsKey(name) || (previousName != null && persisted.ContainsKey(previousName)));

            if (!isInDatabase)
            {
                diagnostics.Add(new Diagnostic(name, NotInDatabaseKind,
                    $"'{name}' is declared by the loaded schema assembly but is not present in this database. It is shown as empty; "
                    + "opening a database never adds components to it."));
                continue;
            }

            try
            {
                engine.RegisterComponentByType(type, schemaValidation: SchemaValidationMode.Enforce);
                registered++;
            }
            catch (SchemaDowngradeException sd)
            {
                diagnostics.Add(new Diagnostic(name, "schema_downgrade", sd.Message));
                hadDowngrade = true;
            }
            catch (SchemaValidationException sv)
            {
                diagnostics.Add(new Diagnostic(name, "breaking_change", sv.Diff.FormatDetailedMessage()));
                hadBreakingChange = true;
            }
            catch (SchemaMigrationException sm)
            {
                diagnostics.Add(new Diagnostic(name, "migration_failed", sm.Message));
                hadMigrationFailed = true;
            }
            catch (Exception ex)
            {
                diagnostics.Add(new Diagnostic(name, "schema_error", ex.Message));
                hadOther = true;
            }
        }

        var state = ClassifyAggregate(registered, hadDowngrade, hadMigrationFailed, hadBreakingChange, hadOther);

        // Errors first, informational last. The MigrationRequired banner renders only `diagnostics.slice(0, 3)`, so a
        // database with one real breaking change and a dozen merely-absent components would otherwise show three "not in
        // this database" notes and bury the thing the user actually has to act on. Stable within each group, so the
        // per-component order is otherwise unchanged.
        var ordered = diagnostics
            .OrderBy(d => d.Kind == NotInDatabaseKind ? 1 : 0)
            .ToArray();

        return new Result(state, ordered, registered);
    }

    /// <summary>
    /// Classify the overall session state from per-component outcomes. Key principle: <see cref="State.Incompatible"/>
    /// means "the session is unusable" — reserve it for <b>total</b> failure or unrecoverable errors (downgrade /
    /// migration-failed) which imply the on-disk data is mismatched vs. binaries in ways the user cannot navigate
    /// around. A mix of successes + per-component errors is <see cref="State.MigrationRequired"/>: the UI can still
    /// show the components that loaded while warning about the ones that didn't.
    /// </summary>
    private static State ClassifyAggregate(
        int registered,
        bool hadDowngrade,
        bool hadMigrationFailed,
        bool hadBreakingChange,
        bool hadOther)
    {
        // Catastrophic kinds: the session is not navigable regardless of how many siblings loaded.
        if (hadDowngrade || hadMigrationFailed)
        {
            return State.Incompatible;
        }
        // Total failure: no component registered at all.
        if (registered == 0 && (hadBreakingChange || hadOther))
        {
            return State.Incompatible;
        }
        // Partial: at least one succeeded, but something needs attention.
        if (hadBreakingChange || hadOther)
        {
            return State.MigrationRequired;
        }
        return State.Ready;
    }
}

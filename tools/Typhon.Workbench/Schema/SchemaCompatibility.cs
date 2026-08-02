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

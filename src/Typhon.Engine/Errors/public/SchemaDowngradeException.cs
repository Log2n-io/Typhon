using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Thrown when the database contains component data written by a newer application version.
/// The persisted schema revision is higher than the runtime revision — a downgrade is not supported.
/// The engine must not open the database to prevent data corruption.
/// </summary>
[PublicAPI]
public class SchemaDowngradeException : TyphonException
{
    /// <summary>Name of the component whose persisted revision is newer than the runtime.</summary>
    public string ComponentName { get; }

    /// <summary>Schema revision found in the database (the newer one).</summary>
    public int PersistedRevision { get; }

    /// <summary>Schema revision the running binary supports (the older one).</summary>
    public int RuntimeRevision { get; }

    /// <summary>
    /// Creates a new <see cref="SchemaDowngradeException"/> for a component whose persisted revision exceeds the runtime revision.
    /// </summary>
    /// <param name="componentName">Name of the component with the newer persisted revision.</param>
    /// <param name="persistedRevision">Schema revision found in the database.</param>
    /// <param name="runtimeRevision">Schema revision the running binary supports.</param>
    public SchemaDowngradeException(string componentName, int persistedRevision, int runtimeRevision) : base(TyphonErrorCode.SchemaValidation,
            $"Database component '{componentName}' has schema revision {persistedRevision} but the runtime binary has revision {runtimeRevision}. " +
            $"The database was written by a newer application version. Use the correct binary or upgrade the application.")
    {
        ComponentName = componentName;
        PersistedRevision = persistedRevision;
        RuntimeRevision = runtimeRevision;
    }
}

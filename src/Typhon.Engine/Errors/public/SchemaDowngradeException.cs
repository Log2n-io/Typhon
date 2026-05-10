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
    public string ComponentName { get; }
    public int PersistedRevision { get; }
    public int RuntimeRevision { get; }

    public SchemaDowngradeException(string componentName, int persistedRevision, int runtimeRevision) : base(TyphonErrorCode.SchemaValidation,
            $"Database component '{componentName}' has schema revision {persistedRevision} but the runtime binary has revision {runtimeRevision}. " +
            $"The database was written by a newer application version. Use the correct binary or upgrade the application.")
    {
        ComponentName = componentName;
        PersistedRevision = persistedRevision;
        RuntimeRevision = runtimeRevision;
    }
}

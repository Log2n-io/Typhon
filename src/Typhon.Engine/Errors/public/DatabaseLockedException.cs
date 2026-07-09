using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// Thrown when a database file is already locked by another process.
/// </summary>
[PublicAPI]
public class DatabaseLockedException : StorageException
{
    /// <summary>
    /// Creates a new <see cref="DatabaseLockedException"/> describing the process that currently owns the lock.
    /// </summary>
    /// <param name="databasePath">Path of the database whose lock could not be acquired.</param>
    /// <param name="ownerPid">PID of the process that holds the lock.</param>
    /// <param name="ownerMachine">Machine name of the process that holds the lock.</param>
    /// <param name="startedAt">When the owning process started.</param>
    public DatabaseLockedException(string databasePath, int ownerPid, string ownerMachine, DateTimeOffset startedAt) : base(TyphonErrorCode.DatabaseLocked,
            $"Database '{databasePath}' is locked by process {ownerPid} on '{ownerMachine}' (started {startedAt:u}). " +
            $"Close the other process or delete the .lock file if the process has crashed.")
    {
        OwnerPid = ownerPid;
        OwnerMachine = ownerMachine;
        StartedAt = startedAt;
    }

    /// <summary>
    /// Creates a new <see cref="DatabaseLockedException"/> describing the owning process, wrapping the underlying failure.
    /// </summary>
    /// <param name="databasePath">Path of the database whose lock could not be acquired.</param>
    /// <param name="ownerPid">PID of the process that holds the lock.</param>
    /// <param name="ownerMachine">Machine name of the process that holds the lock.</param>
    /// <param name="startedAt">When the owning process started.</param>
    /// <param name="innerException">The underlying exception raised while attempting to acquire the lock.</param>
    public DatabaseLockedException(string databasePath, int ownerPid, string ownerMachine, DateTimeOffset startedAt, Exception innerException) :
        base(TyphonErrorCode.DatabaseLocked,
            $"Database '{databasePath}' is locked by process {ownerPid} on '{ownerMachine}' (started {startedAt:u}). " +
            $"Close the other process or delete the .lock file if the process has crashed.",
            innerException)
    {
        OwnerPid = ownerPid;
        OwnerMachine = ownerMachine;
        StartedAt = startedAt;
    }

    /// <summary>PID of the process that holds the lock.</summary>
    public int OwnerPid { get; }

    /// <summary>Machine name of the process that holds the lock.</summary>
    public string OwnerMachine { get; }

    /// <summary>When the owning process started.</summary>
    public DateTimeOffset StartedAt { get; }
}

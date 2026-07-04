# Database File Locking & Lifecycle
> Two-layer protection against two processes opening the same database file, plus safe create/open/delete handling.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Storage](./README.md)

## 🎯 What it solves

Two processes (or two `DatabaseEngine` instances in the same process) writing to the same `.bin` file would corrupt it — there's no merge, no second writer protocol. Typhon needs to refuse a second writer outright, and refuse it with enough information that an operator can act: which process holds it, on which machine, since when. It also needs file create/open/delete to behave correctly on Windows, where a deleted file's directory entry can briefly linger after `File.Delete` returns, which would otherwise break an immediate re-create of the same database name.

## ⚙️ How it works (in brief)

Opening a database acquires two independent locks. The OS-level one is the real guarantee: the data file handle is opened with `FileShare.Read`, so a second process requesting read-write access is rejected by the OS kernel (mandatory on Windows; advisory-only on POSIX). The advisory one is a JSON sidecar, `<DatabaseName>.lock`, written next to the data file with the owning process's PID, machine name, and start time — its only job is to turn an opaque OS sharing-violation into a `DatabaseLockedException` that names the culprit. The lock file is acquired before the data file is opened and removed on `Dispose()`; a dead owner's lock is detected (PID no longer running) and cleared automatically rather than blocking the new open. Deleting a database file polls for NTFS's deferred directory-entry removal so a delete-then-recreate of the same name doesn't race the filesystem.

## 💻 Usage

```csharp
var services = new ServiceCollection();
services.AddTyphon(o => o.DatabaseFile(@"C:\Data\Saves\GameWorld.typhon"));

var sp = services.BuildServiceProvider();

try
{
    var dbe = sp.GetRequiredService<DatabaseEngine>();
    // ... normal ECS/transaction work
}
catch (DatabaseLockedException ex)
{
    log.LogError("database held by PID {Pid} on '{Machine}' since {StartedAt:u}",
        ex.OwnerPid, ex.OwnerMachine, ex.StartedAt);
}

// Wipe a database (data + lock file), waiting out NTFS deferred-delete:
sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
```

| Scenario | Behavior |
|---|---|
| Lock file owner PID alive, **same machine** | `DatabaseLockedException` (`OwnerPid`, `OwnerMachine`, `StartedAt`) |
| Lock file owner PID dead, same machine | Stale — logged, removed, open proceeds |
| Lock file from a **different machine name** | Can't verify a remote PID — always treated as live, throws |
| Lock file corrupt / unreadable JSON | Logged, removed, open proceeds |
| Lock file write itself fails (e.g. read-only dir) | Logged, open proceeds — the OS `FileShare.Read` layer is still active |

## ⚠️ Guarantees & limits

- The OS `FileShare.Read` handle is the actual enforcement layer and cannot be bypassed by deleting `.lock` — on Windows a second writer's `File.OpenHandle` throws `IOException` regardless of the sidecar's state.
- On POSIX, OS file sharing is advisory only (the kernel doesn't block a second writer), so the `.lock` file is the *only* cross-process guard there — don't rely on this feature for safety on a network filesystem shared between hosts that don't see each other's processes.
- A stale lock from a crashed process on the **same machine** is detected and cleared automatically (`Process.GetProcessById` liveness check) — no manual cleanup needed after an ungraceful shutdown.
- A lock file from a **different machine name** is always treated as live and blocks the open, even if that machine is long gone — there's no way to verify a remote PID. Clearing it (delete the `.lock` file) is a manual, deliberate operation after confirming the other machine truly isn't using the file.
- PID reuse is a narrow theoretical gap: if a crashed owner's PID is later reassigned by the OS to an unrelated live process before the next open, that lock would be (incorrectly) treated as live. In practice this requires PID exhaustion/wraparound in the window between crash and reopen.
- `Dispose()` always releases the lock file, even on an initialization failure that occurs after it was acquired — a failed open doesn't leave a phantom lock behind.
- `PagedMMFOptions.EnsureFileDeleted()` (and the `IServiceProvider.EnsureFileDeleted<TO>()` DI helper) remove both the `.bin` and `.lock` files and poll for NTFS's deferred-delete to actually complete; `DeleteDatabaseFile()` only removes the data file and does not wait — prefer `EnsureFileDeleted` before recreating a database under the same name.

## 🧪 Tests

- [DatabaseFileLockingTests](../../../test/Typhon.Engine.Tests/Storage/DatabaseFileLockingTests.cs) — full scenario matrix: stale/live/cross-machine/corrupt lock files, `FileShare` enforcement, `EnsureFileDeleted`

## 🔗 Related

- Source: `src/Typhon.Engine/Storage/internals/PagedMMF.cs` (`AcquireLockFile`/`ReleaseLockFile`/`DeleteFileAndWait`), `src/Typhon.Engine/Storage/public/PagedMMFOptions.cs` (`EnsureFileDeleted`)

<!-- Overview: claude/overview/03-storage.md — File Locking -->
<!-- Overview: claude/overview/10-errors.md (exception hierarchy, DatabaseLockedException) -->

# Clean-Slate Database Deletion
> Delete a Typhon database's `.typhon` bundle directory before opening it fresh.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Hosting](./README.md)

## 🎯 What it solves

Tests, benchmarks, and tooling need to start each run against an empty database — no leftover
file from a prior run. Reaching into the filesystem by hand means duplicating whatever naming/path
logic the engine uses to locate its backing file, and getting it subtly wrong (missing the lock
file, racing NTFS's deferred delete) causes flaky "file in use" failures on the next open.

## ⚙️ How it works (in brief)

`EnsureFileDeleted<TO>` is an extension on `IServiceProvider`. It opens a DI scope, resolves the
registered `IOptions<TO>` for the paged-file options type you're using (`PagedMMFOptions` or
`ManagedPagedMMFOptions`), and recursively deletes the `{DatabaseName}.typhon` **bundle directory**
(data file, `db.lock`, and `wal/`) at the path those options describe. It reads the same
`DatabaseName`/`DatabaseDirectory` the engine itself will use to open the database, so there is no
separate path logic to keep in sync. Deletion is best-effort — failures (e.g. nothing to delete) are
swallowed, not thrown.

## 💻 Usage

```csharp
using Microsoft.Extensions.DependencyInjection;
using Typhon.Engine;

var services = new ServiceCollection();
services.AddTyphon(o => o.DatabaseFile(Path.Combine(testDirectory, "MyTestDb.typhon")));

var sp = services.BuildServiceProvider();

// Before the first GetRequiredService<DatabaseEngine>() call — wipe any leftover bundle.
sp.EnsureFileDeleted<ManagedPagedMMFOptions>();

var engine = sp.GetRequiredService<DatabaseEngine>();
```

## ⚠️ Guarantees & limits

- **Options-driven, not path-guessing.** Resolves the exact `IOptions<TO>` the engine would use,
  so the deleted bundle always matches what a subsequent `AddTyphon` / `AddManagedPagedMMF`
  resolution would open.
- **Deletes the whole bundle.** The `{name}.typhon` directory — data file, `db.lock`, and `wal/` —
  is removed as a unit, so a stale lock can't block reopening.
- **Waits out NTFS deferred delete.** Polls briefly after the recursive `Directory.Delete` so a
  subsequent create at the same path doesn't race the OS's pending-delete state.
- **Best-effort, silent on failure.** Errors (e.g. no file existed) are caught and ignored —
  this is a convenience for clean-slate setup, not a verified-delete guarantee. Do not use it to
  assert deletion succeeded.
- **Must be called before the engine opens the file.** Calling it against a `DatabaseEngine`/
  `ManagedPagedMMF` that already holds the file open will not release that handle first.
- **Generic parameter must match the registered options type** (`PagedMMFOptions` for
  `AddPagedMemoryMappedFile`, `ManagedPagedMMFOptions` for `AddManagedPagedMMF`) — resolving the
  wrong `TO` throws if it isn't registered in the container.

## 🧪 Tests

- [DatabaseFileLockingTests](../../../test/Typhon.Engine.Tests/Storage/DatabaseFileLockingTests.cs) — `EnsureDeleted_RemovesLock` asserts both the `.bin` and `.lock` files are removed; every other test in the fixture calls `sp.EnsureFileDeleted<ManagedPagedMMFOptions>()` as its clean-slate setup step

## 🔗 Related

- Source: [`TyphonBuilderExtensions.cs`](../../../src/Typhon.Engine/Hosting/public/TyphonBuilderExtensions.cs) (`EnsureFileDeleted<TO>`), [`PagedMMFOptions.cs`](../../../src/Typhon.Engine/Storage/public/PagedMMFOptions.cs) (`EnsureFileDeleted`)
- Sibling: [Engine Options Configuration Surface](engine-options-configuration/README.md) — resolves the same `IOptions<TO>` this feature deletes the backing file for.

<!-- Deep dive: claude/design/Hosting/di-extensions.md, claude/overview/03-storage.md -->

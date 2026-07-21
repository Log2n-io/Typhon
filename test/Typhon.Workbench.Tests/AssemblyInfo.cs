using NUnit.Framework;

// Enable parallel test execution at the fixture (class) level — different test classes run concurrently,
// tests within a class stay sequential. Previously ABSENT, so the whole suite ran serially (wall ≈ sum).
//
// This is now safe: the process-global ArchetypeRegistry race that historically made engine fixtures flaky
// (concurrent InitializeArchetypes → IndexOutOfRange in RebuildEntityMapsFromPersistedData + false cascade
// diamond) was closed in PR #530 / commit 3f3b88b (2026-07-19): registry mutations are serialized under
// RegistrationLock, hot read maps are ConcurrentDictionary, per-engine routing tables are fixed-size, and
// the cascade graph is built once under the lock. The engine suite already runs 4-wide on the same registry.
//
// Fixtures that assert exact registry lifecycle / ALC-unload transitions keep their own [NonParallelizable]
// (e.g. SessionLifecycleTests) — they must not race with peer fixtures' registrations.
[assembly: Parallelizable(ParallelScope.Fixtures)]

// 4 workers — mirrors the engine suite. Each Workbench integration fixture spins an ASP.NET host + a
// DatabaseEngine (page cache pinned per engine), so keep parallelism bounded to stay memory-safe; CI can
// raise it via the NUnit.NumberOfTestWorkers env var / .runsettings if the box has the RAM + cores.
[assembly: LevelOfParallelism(4)]

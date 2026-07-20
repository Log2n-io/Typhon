using NUnit.Framework;

// Enable parallel test execution at the fixture (class) level.
// Tests within a single class run sequentially, but different test classes run concurrently.
// This preserves any intra-class ordering ([Order] attributes) while parallelizing across classes.
[assembly: Parallelizable(ParallelScope.Fixtures)]

// Note: LevelOfParallelism requires a compile-time constant.
// For dynamic configuration (e.g., ProcessorCount / 2), use the .runsettings file or the NUnit.NumberOfTestWorkers environment variable.
// 4 workers. The file-I/O cause of 8-way flakiness (NTFS/temp-root contention) is now fixed — every fixture that builds its own MMF uses an isolated per-fixture
// directory (AllocatorTestBase.TestDatabaseDir / TestBase._testDatabaseDir) — but the second cause remains: a handful of Runtime fixtures assert exact
// per-tick scheduler behaviour, and 8-way CPU contention perturbs their tick cadence (OverloadThrottleTests, CheckerboardTests, …). Those keep their own
// [NonParallelizable]; 4 workers stay robust and capture ~17% of the wall-clock 8 would give without the tick-timing fragility. The JIT warmup fixture
// (AssemblyWarmup.cs) pre-compiles hot code paths before parallel execution begins.
[assembly: LevelOfParallelism(4)]

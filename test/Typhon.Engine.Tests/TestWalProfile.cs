using System.IO;

namespace Typhon.Engine.Tests;

/// <summary>
/// Shared WAL configuration for the test suite. With the no-WAL engine mode removed, every test engine runs the real WAL + checkpoint pipeline; this profile
/// points it at an in-memory file-IO backend with FUA disabled so the suite stays fast (no disk I/O, no per-write fsync) while still exercising the exact
/// durability path production uses. The checkpoint cadence keeps the page-cache DirtyCounter draining so a minimum-size cache never exhausts (the #382/#383
/// failure mode that motivated the removal).
/// </summary>
internal static class TestWalProfile
{
    /// <summary>
    /// WAL writer options for tests: FUA off (no per-write fsync) and a WAL directory under the fixture's temp dir. With an in-memory file-IO backend the
    /// directory is only used as an addressing key — no real files are created.
    /// </summary>
    public static WalWriterOptions Fast(string baseDir) => new WalWriterOptions
    {
        WalDirectory = Path.Combine(baseDir, "wal"),
        UseFUA = false,
    };

    /// <summary>
    /// Applies the fast test durability profile to engine options: the <see cref="Fast"/> WAL writer config, an aggressive checkpoint interval (so the
    /// background checkpoint thread drains dirty pages promptly), and a generous WAL ring buffer (so high-write stress fixtures don't hit WAL backpressure).
    /// </summary>
    public static void Apply(DatabaseEngineOptions o, string baseDir)
    {
        o.Wal = Fast(baseDir);
        o.Resources.CheckpointIntervalMs = 100;
        o.Resources.WalRingBufferSizeBytes = 64 * 1024 * 1024;
    }
}

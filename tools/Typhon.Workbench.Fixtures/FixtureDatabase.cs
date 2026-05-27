using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Engine;

namespace Typhon.Workbench.Fixtures;

/// <summary>
/// Result of a call to <see cref="FixtureDatabase.CreateOrReuse"/>. Communicates the path of the generated
/// <c>.typhon</c> marker, the accompanying schema DLL (copied alongside for the Workbench's convention loader),
/// and whether this call actually produced new content.
/// </summary>
/// <param name="TyphonFilePath">Absolute path to the <c>.typhon</c> marker file that the Workbench opens.</param>
/// <param name="SchemaDllPath">Absolute path to the copied <c>*.schema.dll</c> for the fixture components.</param>
/// <param name="TotalEntities">Number of entities spawned during generation (0 when reusing an existing database).</param>
/// <param name="WasCreated"><c>true</c> if the database was (re)created on this call, <c>false</c> if an existing one was reused.</param>
internal readonly record struct FixtureGenerationResult(
    string TyphonFilePath,
    string SchemaDllPath,
    int TotalEntities,
    bool WasCreated);

/// <summary>
/// Configurable shape of a generated Dev Fixture database — per-archetype entity counts, fragmentation behaviour,
/// schema subset, and RNG seed. The Workbench's Dev Fixture dialog builds this from presets ("Tiny", "Default",
/// "Stress", "Fragmented", "Empty-cores", "Custom") and the user's Advanced-form tweaks; the server hashes the
/// canonical JSON of this record to key the on-disk cache, so the same config always reuses, a different config
/// always regenerates. Defaults to <see cref="Default"/> when no client-supplied config is present — keeps every
/// existing caller (E2E specs, the original 2-arg <see cref="FixtureDatabase.CreateOrReuse"/> signature, the manual
/// <c>WorkbenchFixtureGenerator</c> NUnit case) running unchanged.
/// </summary>
public sealed record FixtureConfig(
    int CompAArchCount,
    int CompABArchCount,
    int CompABCArchCount,
    int CompDArchCount,
    int GuildArchCount,
    int PlayerArchCount,
    int ParticleArchCount,
    /// <summary>Fraction of spawned Particles destroyed post-spawn, in [0, 1]. Seeds the cluster half-empty rendering signal.</summary>
    double ParticleFragmentation,
    /// <summary>RNG seed driving all randomised fields. Same seed + same config ⇒ byte-identical generated DB.</summary>
    int Seed)
{
    /// <summary>The baseline preset — preserves the hardcoded counts that shipped before configurability landed.</summary>
    public static FixtureConfig Default { get; } = new(
        CompAArchCount:        1_000,
        CompABArchCount:         500,
        CompABCArchCount:        500,
        CompDArchCount:          200,
        GuildArchCount:           50,
        PlayerArchCount:         300,
        ParticleArchCount:     2_000,
        ParticleFragmentation:   0.40,
        Seed:           123_456_789);

    /// <summary>Total entity count expected from this config (before particle destroys).</summary>
    public int TotalSpawnEstimate
        => CompAArchCount + CompABArchCount + CompABCArchCount + CompDArchCount
           + GuildArchCount + PlayerArchCount + ParticleArchCount;

    /// <summary>
    /// First 8 hex chars of SHA-256 over the canonical JSON of this config. Stable across runs. Used as the on-disk
    /// cache-key suffix on the database name so different configs coexist without collision; same config reuses on
    /// repeat clicks. 8 chars = 32 bits = collision probability under realistic preset count is negligible.
    /// </summary>
    public string Hash()
    {
        var canonical = JsonSerializer.Serialize(this, _hashSerializer);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        var sb = new StringBuilder(8);
        for (int i = 0; i < 4; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }

    private static readonly JsonSerializerOptions _hashSerializer = new()
    {
        // Stable key order via the record's declaration order; floating-point exact serialisation; no whitespace.
        WriteIndented = false,
    };
}

/// <summary>
/// Progress update reported from inside <see cref="FixtureDatabase.CreateOrReuse"/> via <see cref="IProgress{T}"/>.
/// The Workbench's job-registry polling endpoint surfaces these directly to the UI's progress bar.
/// </summary>
/// <param name="Phase">Human-readable label of the current step ("Spawning Players", "Destroying particle subset", …).</param>
/// <param name="Completed">Entities (or sub-units) completed within this phase.</param>
/// <param name="Total">Total entities (or sub-units) for this phase. <c>0</c> = indeterminate / instant phase.</param>
public readonly record struct FixtureProgressReport(string Phase, int Completed, int Total);

/// <summary>
/// Workbench dev-fixture database generator. Populates a configurable set of archetypes (<c>CompAArch</c> …
/// <c>PlayerArch</c>) with deterministic random entity data so the Workbench has real content to browse while we
/// iterate on its UI.
///
/// The method is <c>internal</c>'s public-friend surface — the Workbench Controllers project consumes it via
/// <c>InternalsVisibleTo</c>. A user schema DLL shipped to the Workbench should never offer to bulk-populate a
/// database.
///
/// Extend <see cref="Populate"/> over time with more archetype shapes, edge-case indexes, etc. — every callsite reuses
/// it. Force-recreation bypasses the cache regardless of config match.
/// </summary>
internal static class FixtureDatabase
{
    public const string DefaultDatabaseName = "base-tests";
    private const string ConfigHashFileName = ".fixture-config-hash";

    /// <summary>
    /// Valid characters for a fixture database name: letters, digits, hyphen, underscore. No spaces, dots, or path
    /// separators — the name flows directly into a filename and we don't want users escaping the output directory or
    /// colliding with the <c>.typhon</c> / <c>.bin</c> extensions.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex DatabaseNameRegex
        = new(@"^[a-zA-Z0-9_-]{1,64}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Validate a candidate fixture database name. Returns <c>true</c> + the trimmed/sanitised value on success;
    /// returns <c>false</c> + <paramref name="error"/> populated on failure. Public-friend surface so the controller
    /// can surface the same diagnostic to the client as <c>CreateOrReuse</c> would otherwise throw.
    /// </summary>
    internal static bool TryValidateDatabaseName(string candidate, out string sanitised, out string error)
    {
        sanitised = (candidate ?? string.Empty).Trim();
        if (sanitised.Length == 0)
        {
            error = "Database name must not be empty.";
            return false;
        }
        if (!DatabaseNameRegex.IsMatch(sanitised))
        {
            error = "Database name must be 1–64 chars and use only letters, digits, '-', or '_' (no spaces / dots / slashes).";
            return false;
        }
        error = null;
        return true;
    }

    /// <summary>
    /// Ensure the Workbench test database exists under <paramref name="outputDir"/>. Reuse semantics:
    ///   - <paramref name="force"/> = <c>true</c> → wipe + regenerate unconditionally.
    ///   - <paramref name="force"/> = <c>false</c> AND a previous run with the SAME <paramref name="config"/> hash
    ///     still exists on disk → reuse as-is, no entities spawned.
    ///   - <paramref name="force"/> = <c>false</c> AND no previous run, OR a previous run with a DIFFERENT config
    ///     hash → wipe + regenerate (the config changed; the existing DB doesn't match the user's request).
    ///
    /// <paramref name="databaseName"/> drives the on-disk filenames (<c>{name}.typhon</c> + <c>{name}.bin</c>) AND the
    /// containing folder: every fixture is materialised under <c>{outputDir}/{databaseName}/</c> so multiple databases
    /// can safely share an <paramref name="outputDir"/> root without colliding on the shared <c>*.schema.dll</c> /
    /// <c>config-hash</c> sidecar files. <see cref="PrepareOutputDirectory"/> wipes the per-database sub-directory
    /// wholesale on regenerate; siblings in <paramref name="outputDir"/> are untouched.
    ///
    /// Progress is reported via <paramref name="progress"/> at sub-batch granularity (every ~10k entities or once per
    /// phase boundary, whichever is finer). Cancellation via <paramref name="ct"/> is checked between sub-batches.
    /// </summary>
    internal static FixtureGenerationResult CreateOrReuse(
        string outputDir,
        bool force,
        FixtureConfig config = null,
        IProgress<FixtureProgressReport> progress = null,
        CancellationToken ct = default,
        string databaseName = DefaultDatabaseName,
        bool useBulkLoad = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputDir);
        if (!TryValidateDatabaseName(databaseName, out var safeName, out var nameError))
        {
            throw new ArgumentException(nameError, nameof(databaseName));
        }
        var cfg = config ?? FixtureConfig.Default;

        // Materialise each fixture inside its own per-database sub-directory so the shared sidecars
        // (`Typhon.Workbench.Fixtures.schema.dll`, the `config-hash`) don't collide when multiple databases share an
        // `outputDir` root — see the xmldoc above. `PrepareOutputDirectory` wipes this leaf wholesale on regenerate,
        // so this scoping is also what keeps a force-regen of DB-A from blowing away DB-B's files.
        var absOut = Path.Combine(Path.GetFullPath(outputDir), safeName);
        var typhonPath = Path.Combine(absOut, $"{safeName}.typhon");
        var binPath = Path.Combine(absOut, $"{safeName}.bin");
        var schemaDllPath = Path.Combine(absOut, "Typhon.Workbench.Fixtures.schema.dll");
        var configHashPath = Path.Combine(absOut, ConfigHashFileName);
        var requestedHash = cfg.Hash();

        // Reuse only when nothing has changed. Force = unconditional wipe. Different config hash = treat as a fresh
        // request (the old DB doesn't match what the user is now asking for — silently reusing would mislead them).
        var databaseExists = File.Exists(typhonPath) && File.Exists(binPath);
        var hashMatches = databaseExists && File.Exists(configHashPath)
            && string.Equals(File.ReadAllText(configHashPath).Trim(), requestedHash, StringComparison.Ordinal);
        if (databaseExists && hashMatches && !force)
        {
            return new FixtureGenerationResult(typhonPath, schemaDllPath, TotalEntities: 0, WasCreated: false);
        }

        progress?.Report(new FixtureProgressReport("Preparing directory", 0, 0));
        PrepareOutputDirectory(absOut);
        ct.ThrowIfCancellationRequested();

        Archetype<CompAArch>.Touch();
        Archetype<CompABArch>.Touch();
        Archetype<CompABCArch>.Touch();
        Archetype<CompDArch>.Touch();
        Archetype<GuildArch>.Touch();
        Archetype<PlayerArch>.Touch();
        Archetype<ParticleArch>.Touch();

        using (var sp = BuildEngineServices(absOut, safeName))
        using (var engine = sp.GetRequiredService<DatabaseEngine>())
        {
            engine.RegisterComponentFromAccessor<CompA>();
            engine.RegisterComponentFromAccessor<CompB>();
            engine.RegisterComponentFromAccessor<CompC>();
            engine.RegisterComponentFromAccessor<CompD>();
            engine.RegisterComponentFromAccessor<CompGuild>();
            engine.RegisterComponentFromAccessor<CompPlayer>();
            engine.RegisterComponentFromAccessor<CompParticle>();
            engine.InitializeArchetypes();

            if (useBulkLoad)
            {
                PopulateBulk(engine, cfg, progress, ct);
            }
            else
            {
                Populate(engine, cfg, progress, ct);
            }

            progress?.Report(new FixtureProgressReport("Checkpointing", 0, 0));
            // Force a checkpoint so WAL records are applied to the data file before dispose runs PersistArchetypeState /
            // PersistEngineState. Without this, the reopen path could find stale EntityMapSPI values.
            engine.ForceCheckpoint();
        }

        progress?.Report(new FixtureProgressReport("Writing marker + schema DLL", 0, 0));
        WriteTyphonMarker(absOut, safeName);
        CopySchemaDll(absOut);
        File.WriteAllText(configHashPath, requestedHash);

        int total = cfg.TotalSpawnEstimate;
        return new FixtureGenerationResult(typhonPath, schemaDllPath, total, WasCreated: true);
    }

    /// <summary>
    /// Per-batch size for entity spawn loops. Each batch is one transaction → one commit → DC budget recovers. Bigger
    /// batches = fewer commits but each commit dirties more pages; the page-cache back-pressure budget tops out
    /// around ~75k items in a single tx (project memory: DC inflation issue #133), so 5k leaves plenty of headroom for
    /// every archetype's dirty-page footprint. Stress preset (200k particles) = 40 commits, each ~5k entities, each
    /// trivially under the back-pressure threshold.
    /// </summary>
    private const int BatchSize = 5_000;

    private static void Populate(DatabaseEngine engine, FixtureConfig cfg, IProgress<FixtureProgressReport> progress, CancellationToken ct)
    {
        var rand = new Random(cfg.Seed);

        // ── Guilds (batched) ───────────────────────────────────────────────────────────────────────────────────
        var guildIds = new long[cfg.GuildArchCount];
        SpawnInBatches(cfg.GuildArchCount, "Spawning Guilds", progress, ct, (start, end) =>
        {
            using var tx = engine.CreateQuickTransaction();
            for (int i = start; i < end; i++)
            {
                var g = new CompGuild { Level = rand.Next(1, 60), MemberCap = 100 + i };
                var eid = tx.Spawn<GuildArch>(GuildArch.Guild.Set(in g));
                guildIds[i] = eid.EntityKey;
            }
            if (!tx.Commit()) throw new InvalidOperationException("Guild batch commit failed");
        });

        // ── CompA / AB / ABC / D — each archetype gets its own batched phase. Splitting the previous combined tx
        //    here is the back-pressure fix: one mega-tx with 210k entities (Stress) blows past the page-cache dirty
        //    budget; one commit per 5k entities lets DC drain between batches.
        SpawnInBatches(cfg.CompAArchCount, "Spawning CompA archetypes", progress, ct, (start, end) =>
        {
            using var tx = engine.CreateQuickTransaction();
            for (int i = start; i < end; i++)
            {
                var a = new CompA(rand.Next(), (float)rand.NextDouble(), rand.NextDouble());
                tx.Spawn<CompAArch>(CompAArch.A.Set(in a));
            }
            if (!tx.Commit()) throw new InvalidOperationException("CompA batch commit failed");
        });

        SpawnInBatches(cfg.CompABArchCount, "Spawning CompAB archetypes", progress, ct, (start, end) =>
        {
            using var tx = engine.CreateQuickTransaction();
            for (int i = start; i < end; i++)
            {
                var a = new CompA(rand.Next(), (float)rand.NextDouble(), rand.NextDouble());
                var b = new CompB(rand.Next(), (float)rand.NextDouble());
                tx.Spawn<CompABArch>(CompABArch.A.Set(in a), CompABArch.B.Set(in b));
            }
            if (!tx.Commit()) throw new InvalidOperationException("CompAB batch commit failed");
        });

        SpawnInBatches(cfg.CompABCArchCount, "Spawning CompABC archetypes", progress, ct, (start, end) =>
        {
            using var tx = engine.CreateQuickTransaction();
            for (int i = start; i < end; i++)
            {
                var a = new CompA(rand.Next(), (float)rand.NextDouble(), rand.NextDouble());
                var b = new CompB(rand.Next(), (float)rand.NextDouble());
                var c = new CompC($"entity-{i:D4}");
                tx.Spawn<CompABCArch>(CompABCArch.A.Set(in a), CompABCArch.B.Set(in b), CompABCArch.C.Set(in c));
            }
            if (!tx.Commit()) throw new InvalidOperationException("CompABC batch commit failed");
        });

        SpawnInBatches(cfg.CompDArchCount, "Spawning CompD archetypes", progress, ct, (start, end) =>
        {
            using var tx = engine.CreateQuickTransaction();
            for (int i = start; i < end; i++)
            {
                var d = new CompD { Weight = (float)rand.NextDouble() * 1000f, Key = i, Raw = rand.NextDouble() };
                tx.Spawn<CompDArch>(CompDArch.D.Set(in d));
            }
            if (!tx.Commit()) throw new InvalidOperationException("CompD batch commit failed");
        });

        // ── Players reference Guilds; falls back to a sentinel 0 when there are no guilds (Empty-cores preset). ──
        SpawnInBatches(cfg.PlayerArchCount, "Spawning Players", progress, ct, (start, end) =>
        {
            using var tx = engine.CreateQuickTransaction();
            for (int i = start; i < end; i++)
            {
                var guildKey = cfg.GuildArchCount > 0
                    ? guildIds[rand.Next(0, guildIds.Length)]
                    : 0L;
                var p = new CompPlayer { GuildId = guildKey, Active = rand.Next(0, 4) != 0 ? 1 : 0 };
                tx.Spawn<PlayerArch>(PlayerArch.Player.Set(in p));
            }
            if (!tx.Commit()) throw new InvalidOperationException("Player batch commit failed");
        });

        // ── Particles (SingleVersion cluster — must batch heavily; 200k in one tx is the canonical back-pressure
        //    blow-up. The destroy pass below operates on the recorded EntityIds in matching-sized batches so each
        //    destroy batch is independently bounded by the DC budget too.) ─────────────────────────────────────────
        var particleIds = new EntityId[cfg.ParticleArchCount];
        SpawnInBatches(cfg.ParticleArchCount, "Spawning Particles", progress, ct, (start, end) =>
        {
            using var tx = engine.CreateQuickTransaction();
            for (int i = start; i < end; i++)
            {
                var p = new CompParticle((float)rand.NextDouble(), (float)rand.NextDouble(), (float)rand.NextDouble());
                particleIds[i] = tx.Spawn<ParticleArch>(ParticleArch.Particle.Set(in p));
            }
            if (!tx.Commit()) throw new InvalidOperationException("Particle spawn batch commit failed");
        });

        // ── Particle fragmentation: destroy a configurable fraction so the clusters end up partially filled. Same
        //    batch-and-commit pattern; the random draw inside the loop produces a binomial distribution around the
        //    target destruction rate, which is exactly the half-empty-cluster signal the File Map A6 L3 needs. ────
        if (cfg.ParticleArchCount > 0 && cfg.ParticleFragmentation > 0.0)
        {
            int targetDestroyCount = (int)Math.Round(cfg.ParticleArchCount * cfg.ParticleFragmentation);
            SpawnInBatches(cfg.ParticleArchCount, "Destroying Particle subset", progress, ct, (start, end) =>
            {
                using var tx = engine.CreateQuickTransaction();
                for (int i = start; i < end; i++)
                {
                    if (rand.NextDouble() < cfg.ParticleFragmentation)
                    {
                        tx.Destroy(particleIds[i]);
                    }
                }
                if (!tx.Commit()) throw new InvalidOperationException("Particle destroy batch commit failed");
            }, customTotal: targetDestroyCount);
        }
    }

    /// <summary>
    /// BulkLoad variant of <see cref="Populate"/>. Uses a single <c>BulkLoadSession</c> for the whole fixture,
    /// skipping per-row WAL entirely (BL-01). Trades per-row recoverability for throughput on the strictly
    /// opt-in path. See <c>claude/design/Durability/BulkLoad/</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why opt-in: the bulk path's durability boundary is the whole session — a crash mid-bulk discards every
    /// entity loaded so far (MVCC voids the pending UoW per UR-03). For multi-million-entity fixtures this is
    /// the only practical option; for small / interactive workloads the regular <see cref="Populate"/> remains
    /// the default.
    /// </para>
    /// <para>
    /// Progress reporting mirrors <see cref="Populate"/>: phase-keyed reports at fixed entity intervals so the
    /// Workbench's stall detector continues to function (no quiet stretches longer than the polling threshold).
    /// </para>
    /// </remarks>
    private static void PopulateBulk(DatabaseEngine engine, FixtureConfig cfg, IProgress<FixtureProgressReport> progress, CancellationToken ct)
    {
        var rand = new Random(cfg.Seed);
        var guildIds = new long[cfg.GuildArchCount];
        var particleIds = new EntityId[cfg.ParticleArchCount];

        progress?.Report(new FixtureProgressReport("Opening bulk session", 0, 0));
        using var bulk = engine.BeginBulkLoad();

        RunBulkPhase(cfg.GuildArchCount, "Spawning Guilds (bulk)", progress, ct, i =>
        {
            var g = new CompGuild { Level = rand.Next(1, 60), MemberCap = 100 + i };
            var eid = bulk.Spawn<GuildArch>(GuildArch.Guild.Set(in g));
            guildIds[i] = eid.EntityKey;
        });

        RunBulkPhase(cfg.CompAArchCount, "Spawning CompA archetypes (bulk)", progress, ct, i =>
        {
            var a = new CompA(rand.Next(), (float)rand.NextDouble(), rand.NextDouble());
            bulk.Spawn<CompAArch>(CompAArch.A.Set(in a));
        });

        RunBulkPhase(cfg.CompABArchCount, "Spawning CompAB archetypes (bulk)", progress, ct, i =>
        {
            var a = new CompA(rand.Next(), (float)rand.NextDouble(), rand.NextDouble());
            var b = new CompB(rand.Next(), (float)rand.NextDouble());
            bulk.Spawn<CompABArch>(CompABArch.A.Set(in a), CompABArch.B.Set(in b));
        });

        RunBulkPhase(cfg.CompABCArchCount, "Spawning CompABC archetypes (bulk)", progress, ct, i =>
        {
            var a = new CompA(rand.Next(), (float)rand.NextDouble(), rand.NextDouble());
            var b = new CompB(rand.Next(), (float)rand.NextDouble());
            var c = new CompC($"entity-{i:D4}");
            bulk.Spawn<CompABCArch>(CompABCArch.A.Set(in a), CompABCArch.B.Set(in b), CompABCArch.C.Set(in c));
        });

        RunBulkPhase(cfg.CompDArchCount, "Spawning CompD archetypes (bulk)", progress, ct, i =>
        {
            var d = new CompD { Weight = (float)rand.NextDouble() * 1000f, Key = i, Raw = rand.NextDouble() };
            bulk.Spawn<CompDArch>(CompDArch.D.Set(in d));
        });

        RunBulkPhase(cfg.PlayerArchCount, "Spawning Players (bulk)", progress, ct, i =>
        {
            var guildKey = cfg.GuildArchCount > 0
                ? guildIds[rand.Next(0, guildIds.Length)]
                : 0L;
            var p = new CompPlayer { GuildId = guildKey, Active = rand.Next(0, 4) != 0 ? 1 : 0 };
            bulk.Spawn<PlayerArch>(PlayerArch.Player.Set(in p));
        });

        RunBulkPhase(cfg.ParticleArchCount, "Spawning Particles (bulk)", progress, ct, i =>
        {
            var p = new CompParticle((float)rand.NextDouble(), (float)rand.NextDouble(), (float)rand.NextDouble());
            particleIds[i] = bulk.Spawn<ParticleArch>(ParticleArch.Particle.Set(in p));
        });

        if (cfg.ParticleArchCount > 0 && cfg.ParticleFragmentation > 0.0)
        {
            int targetDestroyCount = (int)Math.Round(cfg.ParticleArchCount * cfg.ParticleFragmentation);
            RunBulkPhase(cfg.ParticleArchCount, "Destroying Particle subset (bulk)", progress, ct, i =>
            {
                if (rand.NextDouble() < cfg.ParticleFragmentation)
                {
                    bulk.Destroy(particleIds[i]);
                }
            }, customTotal: targetDestroyCount);
        }

        progress?.Report(new FixtureProgressReport("Completing bulk load (sync checkpoint + BulkEnd)", 0, 0));
        bulk.CompleteBulkLoad();
    }

    /// <summary>
    /// Iterate <paramref name="total"/> entities serially (no per-batch transaction), reporting progress every
    /// <see cref="BulkProgressInterval"/> spawns. Cancellation observed at each report boundary. Mirrors
    /// <see cref="SpawnInBatches"/>'s contract for the bulk path: phase label + completed/total updates.
    /// </summary>
    private const int BulkProgressInterval = 5_000;

    private static void RunBulkPhase(
        int total,
        string phaseLabel,
        IProgress<FixtureProgressReport> progress,
        CancellationToken ct,
        Action<int> spawnOne,
        int customTotal = -1)
    {
        if (total <= 0) return;
        var reportedTotal = customTotal >= 0 ? customTotal : total;
        progress?.Report(new FixtureProgressReport(phaseLabel, 0, reportedTotal));

        for (int i = 0; i < total; i++)
        {
            spawnOne(i);

            // Report at fixed intervals so the stall detector observes regular progress motion.
            if ((i + 1) % BulkProgressInterval == 0 || i + 1 == total)
            {
                ct.ThrowIfCancellationRequested();
                var ratio = (double)(i + 1) / total;
                var completed = (int)(ratio * reportedTotal);
                progress?.Report(new FixtureProgressReport(phaseLabel, completed, reportedTotal));
            }
        }
    }

    /// <summary>
    /// Chunk <paramref name="total"/> items into commit-sized batches of <see cref="BatchSize"/>, invoking
    /// <paramref name="batchFn"/> once per batch with the half-open [start, end) index range. Each call MUST open +
    /// commit its own transaction — the back-pressure fix depends on the dirty-counter draining between commits.
    /// Progress is reported BEFORE the batch (so the user sees the phase label early) and AFTER each commit (so they
    /// see motion); cancellation is observed between batches, never mid-batch (commits never roll back here).
    ///
    /// <paramref name="customTotal"/> overrides the progress denominator — used by the Particle-destroy phase, which
    /// iterates over <see cref="FixtureConfig.ParticleArchCount"/> particles but reports progress against the EXPECTED
    /// destroy count (so the bar fills to ~100% rather than capping at the fragmentation %).
    /// </summary>
    private static void SpawnInBatches(
        int total,
        string phaseLabel,
        IProgress<FixtureProgressReport> progress,
        CancellationToken ct,
        Action<int, int> batchFn,
        int customTotal = -1)
    {
        if (total <= 0) return;
        var reportedTotal = customTotal >= 0 ? customTotal : total;
        progress?.Report(new FixtureProgressReport(phaseLabel, 0, reportedTotal));
        for (int start = 0; start < total; start += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var end = Math.Min(start + BatchSize, total);
            batchFn(start, end);
            // Scale progress to the reported total — the destroy phase reports against expected-destroyed, not the
            // total iteration count, so the bar reaches ~100% at the natural endpoint.
            var ratio = (double)end / total;
            var completed = (int)(ratio * reportedTotal);
            progress?.Report(new FixtureProgressReport(phaseLabel, completed, reportedTotal));
        }
    }

    private static void PrepareOutputDirectory(string dir)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "wal"));
    }

    private static ServiceProvider BuildEngineServices(string directory, string databaseName)
    {
        var walDir = Path.Combine(directory, "wal");
        Directory.CreateDirectory(walDir);

        var services = new ServiceCollection();
        services
            .AddLogging(b =>
            {
                b.SetMinimumLevel(LogLevel.Warning);
            })
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(opts =>
            {
                opts.DatabaseName = databaseName;
                opts.DatabaseDirectory = directory;
                // 65536 pages × 8 KB = 512 MiB page cache — matches the Workbench's own open path
                // (EngineLifecycle.cs:116). The 64 MB the fixture started with worked for the Stress preset (420k)
                // but was too small for million-entity scales: the EntityMap (paged hash) alone needs hundreds of
                // pages hot at that count, and adding the component clusters + BTree index pages saturates the
                // cache during Commit (RawValuePagedHashMap.InsertNew → ChunkAccessor.EvictSlot back-pressure).
                opts.DatabaseCacheSize = 65536UL * 8192;
                opts.PagesDebugPattern = false;
            })
            .AddScopedDatabaseEngine(opts =>
            {
                // WAL + CheckpointManager are MANDATORY for any non-trivial Populate run. Reason: the page cache's
                // DirtyCounter only decreases when the background checkpoint thread flushes pages to disk — without
                // WAL there's no CheckpointManager, so DC monotonically grows until the cache is fully dirty and
                // PersistEngineState (during engine Dispose) can't evict a page → PageCacheBackpressureTimeoutException.
                // Repro: any Populate that dirties >8192 distinct cache pages (e.g. the Stress preset). Also matches
                // the Workbench's own engine-open lifecycle (EngineLifecycle.cs:120), so the generated DB is in the
                // same on-disk shape consumers see.
                opts.Wal = new WalWriterOptions
                {
                    WalDirectory = walDir,
                    // FUA off — fixture generation doesn't need per-write durability; group-commit + final fsync is enough.
                    UseFUA = false
                };
                // 100 ms idle interval (vs 30 s default) so the checkpoint thread drains DC aggressively during the
                // long spawn loops. Back-pressure invocations still nudge it on demand; the small idle interval is the
                // backstop for the gaps between nudges.
                opts.Resources.CheckpointIntervalMs = 100;
                // Bump the WAL ring buffer from the 8 MB default to 64 MB — 4M+ entity Populates produce a lot of WAL
                // records very quickly; the small default fills before the WAL writer can drain, triggering
                // WalBackPressureTimeoutException as a second-order failure mode.
                opts.Resources.WalRingBufferSizeBytes = 64 * 1024 * 1024;
            });
        return services.BuildServiceProvider();
    }

    private static void WriteTyphonMarker(string outDir, string databaseName)
    {
        var marker = Path.Combine(outDir, $"{databaseName}.typhon");
        if (!File.Exists(marker))
        {
            File.WriteAllText(marker, string.Empty);
        }
    }

    /// <summary>
    /// Copy the fixture schema DLL (and a defensive set of engine-side dependencies) next to the generated database
    /// so the Workbench's schema-convention loader finds them without the user having to paste any path. The engine
    /// DLLs are resolved from this assembly's base directory — both the test process and the Workbench process
    /// publish them to their bin output.
    /// </summary>
    private static void CopySchemaDll(string outDir)
    {
        var baseDir = Path.GetDirectoryName(typeof(FixtureDatabase).Assembly.Location);
        if (string.IsNullOrEmpty(baseDir))
        {
            return;
        }

        var fixtureDll = Path.Combine(baseDir, "Typhon.Workbench.Fixtures.schema.dll");
        if (!File.Exists(fixtureDll))
        {
            return;
        }
        File.Copy(fixtureDll, Path.Combine(outDir, "Typhon.Workbench.Fixtures.schema.dll"), overwrite: true);

        string[] engineDeps =
        [
            "Typhon.Engine.dll",
            "Typhon.Schema.Definition.dll",
            "Typhon.Protocol.dll",
            "Typhon.Profiler.dll",
        ];
        foreach (var name in engineDeps)
        {
            var src = Path.Combine(baseDir, name);
            var dst = Path.Combine(outDir, name);
            if (File.Exists(src) && !File.Exists(dst))
            {
                try { File.Copy(src, dst); }
                catch (IOException) { /* best-effort */ }
            }
        }
    }
}

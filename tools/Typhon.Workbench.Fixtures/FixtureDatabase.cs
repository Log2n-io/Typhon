using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Engine;
using Typhon.Schema.Definition;

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

/// <summary>How players are distributed across guilds — drives non-unique index density on <c>Membership.Guild</c>.</summary>
public enum PlayersPerGuildShape
{
    /// <summary>Each player picks a guild uniformly at random.</summary>
    Uniform = 0,
    /// <summary>Low-index guilds favoured (power-law) — a few large guilds, many tiny ones.</summary>
    Zipf = 1,
    /// <summary>Most players concentrate in a small fraction of guilds (heavy clumping).</summary>
    Clumped = 2,
}

/// <summary>
/// Configurable shape of a generated Dev Fixture database for the SWG-inspired schema. Three orthogonal axes:
/// <b>volumetry</b> (per-archetype counts), <b>complexity</b> (data-level feature toggles — schema-shape toggles are not
/// possible because Typhon schemas are compile-time), and <b>distribution</b> (entity-mix realism that drives the
/// Enable/Disable + cascade demos). The Workbench's Dev Fixture dialog builds this from presets and the user's tweaks;
/// the server hashes the canonical JSON of this record to key the on-disk cache (same config reuses, different config
/// regenerates). Defaults to <see cref="Default"/> when no client-supplied config is present.
/// </summary>
public sealed record FixtureConfig(
    // ── Volumetry (per-archetype entity counts) ──────────────────────────────────────────────────────────────────────
    /// <summary>ResourceType taxonomy nodes (generated as a tree of depth <see cref="ResourceTaxonomyDepth"/>).</summary>
    int ResourceTypeCount,
    /// <summary>Guilds.</summary>
    int GuildCount,
    /// <summary>Recipes — each gets 1..8 RecipeSlot ComponentCollection elements.</summary>
    int RecipeCount,
    /// <summary>Players (the dominant scale axis). Carry mixed V+SV+Transient storage.</summary>
    int PlayerCount,
    /// <summary>Resource deposits (static-spatial).</summary>
    int DepositCount,
    /// <summary>Harvester structures (polymorphic leaf of Structure).</summary>
    int HarvesterCount,
    /// <summary>Factory structures (polymorphic leaf; only spawned when <see cref="IncludePolymorphicStructure"/>).</summary>
    int FactoryCount,
    /// <summary>Items — highest-cardinality table; each gets 0..<see cref="MaxAffixesPerItem"/> affix CC elements.</summary>
    int ItemCount,
    // ── Complexity (data-level shape toggles) ────────────────────────────────────────────────────────────────────────
    /// <summary>Depth of the ResourceType.Parent taxonomy tree (1 = flat).</summary>
    int ResourceTaxonomyDepth,
    /// <summary>Cap on ItemAffix ComponentCollection elements per item.</summary>
    int MaxAffixesPerItem,
    /// <summary>Distinct values for Player.ProfessionId / Recipe.ProfessionReq.</summary>
    int ProfessionCount,
    /// <summary>If false, items spawn with empty affix collections (single-component items).</summary>
    bool IncludeMultiAffixItems,
    /// <summary>If false, no Factories are spawned (Harvester-only — drops the polymorphic-subtree leaf #2).</summary>
    bool IncludePolymorphicStructure,
    // ── Distribution (entity-mix realism — drives Enable/Disable + cascade demos) ─────────────────────────────────────
    /// <summary>Fraction of players whose Session is left ENABLED (online); the rest are Disabled (offline). [0,1].</summary>
    double OnlinePlayerFraction,
    /// <summary>Fraction of harvesters whose MaintenanceState is Disabled (broken). [0,1].</summary>
    double BrokenHarvesterFraction,
    /// <summary>Fraction of deposits whose Deposit component is Disabled (depleted). [0,1].</summary>
    double DepletedDepositFraction,
    /// <summary>Fraction of factories whose PowerSupply is Disabled (idle). [0,1].</summary>
    double IdleFactoryFraction,
    /// <summary>How players spread across guilds (stresses Membership.Guild index density).</summary>
    PlayersPerGuildShape PlayersPerGuildShape,
    /// <summary>Fraction of players deleted post-spawn — exercises cascade delete of their Items + Structures. [0,1].</summary>
    double DeletedPlayerFraction,
    /// <summary>RNG seed driving all randomised fields. Same seed + same config ⇒ deterministic generated DB.</summary>
    int Seed)
{
    /// <summary>The baseline preset — a modest, fast-to-generate fixture that exercises every engine feature.</summary>
    public static FixtureConfig Default { get; } = new(
        ResourceTypeCount:          60,
        GuildCount:                 50,
        RecipeCount:               200,
        PlayerCount:             2_000,
        DepositCount:            1_000,
        HarvesterCount:            800,
        FactoryCount:              200,
        ItemCount:               5_000,
        ResourceTaxonomyDepth:       3,
        MaxAffixesPerItem:           4,
        ProfessionCount:            16,
        IncludeMultiAffixItems:   true,
        IncludePolymorphicStructure: true,
        OnlinePlayerFraction:     0.15,
        BrokenHarvesterFraction:  0.10,
        DepletedDepositFraction:  0.05,
        IdleFactoryFraction:      0.20,
        PlayersPerGuildShape:     PlayersPerGuildShape.Zipf,
        DeletedPlayerFraction:    0.02,
        Seed:            123_456_789);

    /// <summary>Total entity count expected from this config (before cascade deletes — mirrors the prior estimate
    /// semantics). Structure base (825) is never spawned directly; Factories count only when polymorphism is enabled.</summary>
    public int TotalSpawnEstimate
        => ResourceTypeCount + GuildCount + RecipeCount + PlayerCount + DepositCount + HarvesterCount
           + (IncludePolymorphicStructure ? FactoryCount : 0) + ItemCount;

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
/// Workbench dev-fixture database generator. Populates the 9 SWG-inspired archetypes (<c>Guild</c>, <c>ResourceType</c>,
/// <c>Recipe</c>, <c>Player</c>, <c>ResourceDeposit</c>, <c>Structure</c>←<c>Harvester</c>/<c>Factory</c>, <c>Item</c>)
/// with deterministic random entity data so the Workbench has real, feature-rich content to browse.
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
        // The engine creates the database as a "{safeName}.typhon" bundle directory (data + db.lock + wal/ inside) under
        // absOut. typhonPath is that bundle — the single openable path handed back to the Workbench.
        var typhonPath = Path.Combine(absOut, $"{safeName}.typhon");
        // ADR-055: the schema assembly is no longer copied per-database — it's resolved from the Workbench's own
        // deployment directory at open time. Report that shared, always-current location rather than a per-DB copy.
        var schemaDllPath = Path.Combine(AppContext.BaseDirectory, "Typhon.Workbench.Fixtures.schema.dll");
        var configHashPath = Path.Combine(absOut, ConfigHashFileName);
        var requestedHash = cfg.Hash();

        // Reuse only when nothing has changed. Force = unconditional wipe. Different config hash = treat as a fresh
        // request (the old DB doesn't match what the user is now asking for — silently reusing would mislead them).
        var databaseExists = File.Exists(Path.Combine(typhonPath, "data"));
        var hashMatches = databaseExists && File.Exists(configHashPath)
            && string.Equals(File.ReadAllText(configHashPath).Trim(), requestedHash, StringComparison.Ordinal);
        if (databaseExists && hashMatches && !force)
        {
            return new FixtureGenerationResult(typhonPath, schemaDllPath, TotalEntities: 0, WasCreated: false);
        }

        progress?.Report(new FixtureProgressReport("Preparing directory", 0, 0));
        PrepareOutputDirectory(absOut);
        ct.ThrowIfCancellationRequested();

        Archetype<GuildArch>.Touch();
        Archetype<ResourceTypeArch>.Touch();
        Archetype<RecipeArch>.Touch();
        Archetype<PlayerArch>.Touch();
        Archetype<ResourceDepositArch>.Touch();
        Archetype<StructureArch>.Touch();
        Archetype<HarvesterArch>.Touch();
        Archetype<FactoryArch>.Touch();
        Archetype<ItemArch>.Touch();

        using (var sp = BuildEngineServices(absOut, safeName))
        using (var engine = sp.GetRequiredService<DatabaseEngine>())
        {
            engine.RegisterComponentFromAccessor<Guild>();
            engine.RegisterComponentFromAccessor<Membership>();
            engine.RegisterComponentFromAccessor<Player>();
            engine.RegisterComponentFromAccessor<Wallet>();
            engine.RegisterComponentFromAccessor<Session>();
            engine.RegisterComponentFromAccessor<ResourceType>();
            engine.RegisterComponentFromAccessor<Recipe>();
            engine.RegisterComponentFromAccessor<Deposit>();
            engine.RegisterComponentFromAccessor<Structure>();
            engine.RegisterComponentFromAccessor<StructureOwner>();
            engine.RegisterComponentFromAccessor<Hopper>();
            engine.RegisterComponentFromAccessor<HarvesterTarget>();
            engine.RegisterComponentFromAccessor<MaintenanceState>();
            engine.RegisterComponentFromAccessor<FactoryConfig>();
            engine.RegisterComponentFromAccessor<PowerSupply>();
            engine.RegisterComponentFromAccessor<Item>();
            engine.RegisterComponentFromAccessor<ItemOwner>();
            engine.RegisterComponentFromAccessor<PlayerPosition>();
            engine.RegisterComponentFromAccessor<DepositPosition>();
            engine.RegisterComponentFromAccessor<StructurePosition>();

            // Spatial archetypes (Player/Deposit/Harvester/Factory positions) are cluster-eligible, so a configured
            // grid is REQUIRED before InitializeArchetypes (issue #230 Option B). All positions are placed within
            // [0, WorldSize] (see PlaceAabb). The config is persisted, so the Workbench reopens without re-configuring.
            engine.ConfigureSpatialGrid(new SpatialGridConfig(
                new Vector2(0f, 0f), new Vector2(WorldSize, WorldSize), cellSize: WorldCellSize));
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

        // The engine already materialised the "{safeName}.typhon" bundle directory — no separate marker file needed.
        // ADR-055: no schema-DLL copy — the Workbench resolves the (single, current) schema assembly from its own
        // deployment directory on open, so a per-DB copy would only ever go stale.
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

    /// <summary>Spatial world extent — every Position AABB is placed within [0, WorldSize]². Must match the grid
    /// configured in <see cref="CreateOrReuse"/>.</summary>
    private const float WorldSize = 10_000f;

    /// <summary>Spatial grid cell size. ~100 cells per axis at the default world extent.</summary>
    private const float WorldCellSize = 100f;

    /// <summary>Half-extent of each entity's placed AABB box.</summary>
    private const float EntityExtent = 5f;

    private static void Populate(DatabaseEngine engine, FixtureConfig cfg, IProgress<FixtureProgressReport> progress, CancellationToken ct)
    {
        var rand = new Random(cfg.Seed);

        // Spawned EntityIds, kept per-archetype so later phases can wire EntityLink FKs and the post-passes can
        // Open/Disable/Destroy by id. Generation order respects FK dependencies (parents before children).

        // ── ResourceType taxonomy (self-FK Parent → an earlier node; depth-bounded tree) ────────────────────────────
        var resourceTypeIds = new EntityId[cfg.ResourceTypeCount];
        var rtTiers = new int[cfg.ResourceTypeCount];
        SpawnInBatches(cfg.ResourceTypeCount, "Spawning ResourceTypes", progress, ct, (start, end) =>
        {
            using var tx = engine.CreateQuickTransaction();
            for (int i = start; i < end; i++)
            {
                var parent = EntityLink<ResourceTypeArch>.Null;
                int tier = 0;
                // 30% of non-root nodes stay roots; the rest hang off an already-spawned node one tier deeper.
                if (i > 0 && cfg.ResourceTaxonomyDepth > 1 && rand.NextDouble() > 0.30)
                {
                    int p = rand.Next(0, i);
                    parent = resourceTypeIds[p];
                    tier = Math.Min(rtTiers[p] + 1, cfg.ResourceTaxonomyDepth - 1);
                }
                rtTiers[i] = tier;
                var rt = new ResourceType { Name = S64($"Res-{i}"), Tier = tier, Parent = parent };
                resourceTypeIds[i] = tx.Spawn<ResourceTypeArch>(ResourceTypeArch.ResourceType.Set(in rt));
            }
            if (!tx.Commit()) throw new InvalidOperationException("ResourceType batch commit failed");
        });

        // ── Guilds ───────────────────────────────────────────────────────────────────────────────────────────────
        var guildIds = new EntityId[cfg.GuildCount];
        SpawnInBatches(cfg.GuildCount, "Spawning Guilds", progress, ct, (start, end) =>
        {
            using var tx = engine.CreateQuickTransaction();
            for (int i = start; i < end; i++)
            {
                var g = new Guild { Name = S64($"Guild-{i}"), Faction = rand.Next(0, 4), MemberCount = rand.Next(0, 500), Treasury = rand.Next() };
                guildIds[i] = tx.Spawn<GuildArch>(GuildArch.Guild.Set(in g));
            }
            if (!tx.Commit()) throw new InvalidOperationException("Guild batch commit failed");
        });

        // ── Recipes (FK PrimaryClass → ResourceType; 1..8 RecipeSlot ComponentCollection elements) ─────────────────
        var recipeIds = new EntityId[cfg.RecipeCount];
        int professions = Math.Max(1, cfg.ProfessionCount);
        SpawnInBatches(cfg.RecipeCount, "Spawning Recipes", progress, ct, (start, end) =>
        {
            using var tx = engine.CreateQuickTransaction();
            for (int i = start; i < end; i++)
            {
                var r = new Recipe
                {
                    Name = S64($"Recipe-{i}"),
                    Tier = rand.Next(0, 5),
                    ProfessionReq = rand.Next(0, professions),
                    PrimaryClass = PickLink<ResourceTypeArch>(resourceTypeIds, rand),
                };
                {
                    using var cca = tx.CreateComponentCollectionAccessor(ref r.Slots);
                    int slotCount = rand.Next(1, 9); // 1..8
                    for (int s = 0; s < slotCount; s++)
                    {
                        cca.Add(new RecipeSlot
                        {
                            SlotIndex = s,
                            ClassReq = cfg.ResourceTypeCount > 0 ? rand.Next(cfg.ResourceTypeCount) : -1,
                            MinUnits = rand.Next(1, 100),
                        });
                    }
                }
                recipeIds[i] = tx.Spawn<RecipeArch>(RecipeArch.Recipe.Set(in r));
            }
            if (!tx.Commit()) throw new InvalidOperationException("Recipe batch commit failed");
        });

        // ── Players (mixed V + SV + Transient; FK Membership.Guild → Guild) ─────────────────────────────────────────
        var playerIds = new EntityId[cfg.PlayerCount];
        SpawnInBatches(cfg.PlayerCount, "Spawning Players", progress, ct, (start, end) =>
        {
            using var tx = engine.CreateQuickTransaction();
            for (int i = start; i < end; i++)
            {
                var p = new Player
                {
                    Name = S64($"Player-{i}"), AccountId = i, Level = rand.Next(1, 91),
                    ProfessionId = rand.Next(0, professions), CreatedAt = i,
                };
                var m = new Membership { Guild = PickGuild(guildIds, cfg.PlayersPerGuildShape, rand), GuildRank = rand.Next(0, 6) };
                var w = new Wallet { Credits = rand.Next(0, 1_000_000), BankCredits = rand.Next(0, 100_000_000) };
                var pos = new PlayerPosition { Bounds = PlaceAabb(rand) };
                var sess = new Session { ConnectionId = i, LatencyMs = rand.Next(5, 300) };
                playerIds[i] = tx.Spawn<PlayerArch>(
                    PlayerArch.Player.Set(in p),
                    PlayerArch.Membership.Set(in m),
                    PlayerArch.Wallet.Set(in w),
                    PlayerArch.Position.Set(in pos),
                    PlayerArch.Session.Set(in sess));
            }
            if (!tx.Commit()) throw new InvalidOperationException("Player batch commit failed");
        });

        // ── ResourceDeposits (FK Type → ResourceType; static spatial Position) ──────────────────────────────────────
        var depositIds = new EntityId[cfg.DepositCount];
        SpawnInBatches(cfg.DepositCount, "Spawning ResourceDeposits", progress, ct, (start, end) =>
        {
            using var tx = engine.CreateQuickTransaction();
            for (int i = start; i < end; i++)
            {
                var d = new Deposit
                {
                    Type = PickLink<ResourceTypeArch>(resourceTypeIds, rand),
                    Quality = rand.Next(0, 100), Concentration = rand.Next(0, 100), DepletesAt = rand.Next(),
                };
                var pos = new DepositPosition { Bounds = PlaceAabb(rand) };
                depositIds[i] = tx.Spawn<ResourceDepositArch>(ResourceDepositArch.Deposit.Set(in d), ResourceDepositArch.Position.Set(in pos));
            }
            if (!tx.Commit()) throw new InvalidOperationException("ResourceDeposit batch commit failed");
        });

        // ── Harvesters (polymorphic leaf of Structure; FKs Owner→Player [cascade], Class→ResourceType, Deposit→Deposit) ──
        var harvesterIds = new EntityId[cfg.HarvesterCount];
        SpawnInBatches(cfg.HarvesterCount, "Spawning Harvesters", progress, ct, (start, end) =>
        {
            using var tx = engine.CreateQuickTransaction();
            for (int i = start; i < end; i++)
            {
                var st = new Structure { TypeCode = rand.Next(0, 10), PlacedAt = i, Maintenance = rand.Next(0, 100) };
                var owner = new StructureOwner { Owner = PickLink<PlayerArch>(playerIds, rand) };
                var hop = new Hopper { Class = PickLink<ResourceTypeArch>(resourceTypeIds, rand), Amount = rand.Next(0, 1000), Rate = rand.Next(1, 50) };
                var tgt = new HarvesterTarget { Deposit = PickLink<ResourceDepositArch>(depositIds, rand) };
                var maint = new MaintenanceState { PaidUntil = rand.Next() };
                var pos = new StructurePosition { Bounds = PlaceAabb(rand) };
                harvesterIds[i] = tx.Spawn<HarvesterArch>(
                    StructureArch.Structure.Set(in st),
                    StructureArch.Owner.Set(in owner),
                    HarvesterArch.Hopper.Set(in hop),
                    HarvesterArch.Target.Set(in tgt),
                    HarvesterArch.Maintenance.Set(in maint),
                    HarvesterArch.Position.Set(in pos));
            }
            if (!tx.Commit()) throw new InvalidOperationException("Harvester batch commit failed");
        });

        // ── Factories (second polymorphic leaf; only when enabled. FKs Owner→Player [cascade], Recipe→Recipe) ───────
        var factoryIds = cfg.IncludePolymorphicStructure ? new EntityId[cfg.FactoryCount] : [];
        if (cfg.IncludePolymorphicStructure)
        {
            SpawnInBatches(cfg.FactoryCount, "Spawning Factories", progress, ct, (start, end) =>
            {
                using var tx = engine.CreateQuickTransaction();
                for (int i = start; i < end; i++)
                {
                    var st = new Structure { TypeCode = rand.Next(0, 10), PlacedAt = i, Maintenance = rand.Next(0, 100) };
                    var owner = new StructureOwner { Owner = PickLink<PlayerArch>(playerIds, rand) };
                    var fc = new FactoryConfig { Recipe = PickLink<RecipeArch>(recipeIds, rand), RemainingRuns = rand.Next(0, 1000) };
                    var pw = new PowerSupply { CreditsRemaining = rand.Next() };
                    var pos = new StructurePosition { Bounds = PlaceAabb(rand) };
                    factoryIds[i] = tx.Spawn<FactoryArch>(
                        StructureArch.Structure.Set(in st),
                        StructureArch.Owner.Set(in owner),
                        FactoryArch.Config.Set(in fc),
                        FactoryArch.Power.Set(in pw),
                        FactoryArch.Position.Set(in pos));
                }
                if (!tx.Commit()) throw new InvalidOperationException("Factory batch commit failed");
            });
        }

        // ── Items (FK Recipe→Recipe, Owner→Player [cascade]; 0..MaxAffixesPerItem ItemAffix CC elements) ───────────
        SpawnInBatches(cfg.ItemCount, "Spawning Items", progress, ct, (start, end) =>
        {
            using var tx = engine.CreateQuickTransaction();
            for (int i = start; i < end; i++)
            {
                var it = new Item
                {
                    Recipe = PickLink<RecipeArch>(recipeIds, rand),
                    ItemType = rand.Next(0, 50), Quality = rand.Next(0, 100), Decay = rand.Next(0, 100),
                };
                int affixes = cfg.IncludeMultiAffixItems ? rand.Next(0, cfg.MaxAffixesPerItem + 1) : 0;
                if (affixes > 0)
                {
                    using var cca = tx.CreateComponentCollectionAccessor(ref it.Affixes);
                    for (int a = 0; a < affixes; a++)
                    {
                        cca.Add(new ItemAffix { AffixType = rand.Next(0, 20), Value = rand.Next(1, 100) });
                    }
                }
                var owner = new ItemOwner { Owner = PickLink<PlayerArch>(playerIds, rand) };
                tx.Spawn<ItemArch>(ItemArch.Item.Set(in it), ItemArch.Owner.Set(in owner));
            }
            if (!tx.Commit()) throw new InvalidOperationException("Item batch commit failed");
        });

        // ── Enable/Disable seeding — sets a configurable fraction of each toggleable component to Disabled so the
        //    Query Console's ENABLED/DISABLED clause returns non-empty, varied results. Online = ENABLED fraction. ──
        DisableFraction(engine, playerIds, 1.0 - cfg.OnlinePlayerFraction, PlayerArch.Session, rand, "Disabling offline Sessions", progress, ct);
        DisableFraction(engine, harvesterIds, cfg.BrokenHarvesterFraction, HarvesterArch.Maintenance, rand, "Disabling broken Harvesters", progress, ct);
        if (cfg.IncludePolymorphicStructure)
        {
            DisableFraction(engine, factoryIds, cfg.IdleFactoryFraction, FactoryArch.Power, rand, "Disabling idle Factories", progress, ct);
        }
        DisableFraction(engine, depositIds, cfg.DepletedDepositFraction, ResourceDepositArch.Deposit, rand, "Disabling depleted Deposits", progress, ct);

        // ── Cascade delete — destroy a fraction of players; their Items + Structures cascade away via the
        //    OnParentDelete=Delete FKs, so the registered cascade path actually runs during a default generation. ────
        if (cfg.DeletedPlayerFraction > 0.0 && playerIds.Length > 0)
        {
            SpawnInBatches(playerIds.Length, "Cascade-deleting players", progress, ct, (start, end) =>
            {
                using var tx = engine.CreateQuickTransaction();
                for (int i = start; i < end; i++)
                {
                    if (rand.NextDouble() < cfg.DeletedPlayerFraction)
                    {
                        tx.Destroy(playerIds[i]);
                    }
                }
                if (!tx.Commit()) throw new InvalidOperationException("Cascade-delete batch commit failed");
            });
        }
    }

    // ── Generation helpers ──────────────────────────────────────────────────────────────────────────────────────────

    private static String64 S64(string s)
    {
        String64 v = default;
        v.AsString = s;
        return v;
    }

    /// <summary>Place an entity's AABB as a small box at a random point inside [EntityExtent, WorldSize-EntityExtent]².</summary>
    private static AABB2F PlaceAabb(Random rand)
    {
        float x = (float)rand.NextDouble() * (WorldSize - 2 * EntityExtent) + EntityExtent;
        float y = (float)rand.NextDouble() * (WorldSize - 2 * EntityExtent) + EntityExtent;
        return new AABB2F { MinX = x - EntityExtent, MinY = y - EntityExtent, MaxX = x + EntityExtent, MaxY = y + EntityExtent };
    }

    /// <summary>Pick a random target id as a typed link, or Null when the pool is empty.</summary>
    private static EntityLink<TArch> PickLink<TArch>(EntityId[] ids, Random rand) where TArch : class
    {
        if (ids.Length == 0)
        {
            return EntityLink<TArch>.Null;
        }
        return ids[rand.Next(ids.Length)];
    }

    /// <summary>Pick a guild for a player according to the configured distribution shape.</summary>
    private static EntityLink<GuildArch> PickGuild(EntityId[] guildIds, PlayersPerGuildShape shape, Random rand)
    {
        if (guildIds.Length == 0)
        {
            return EntityLink<GuildArch>.Null;
        }
        int idx = shape switch
        {
            // Squared uniform skews toward index 0 (a few large guilds, a long tail of small ones).
            PlayersPerGuildShape.Zipf => (int)(guildIds.Length * rand.NextDouble() * rand.NextDouble()),
            // 80% of players land in the first 10% of guilds.
            PlayersPerGuildShape.Clumped => rand.NextDouble() < 0.80
                ? rand.Next(Math.Max(1, guildIds.Length / 10))
                : rand.Next(guildIds.Length),
            _ => rand.Next(guildIds.Length),
        };
        if (idx >= guildIds.Length)
        {
            idx = guildIds.Length - 1;
        }
        return guildIds[idx];
    }

    /// <summary>Disable a component on a random <paramref name="fraction"/> of the given entities (batched, one tx per
    /// batch). Used to seed the offline/broken/idle/depleted states the Query Console's enabled-clause filters on.</summary>
    private static void DisableFraction<T>(
        DatabaseEngine engine,
        EntityId[] ids,
        double fraction,
        Comp<T> comp,
        Random rand,
        string label,
        IProgress<FixtureProgressReport> progress,
        CancellationToken ct) where T : unmanaged
    {
        if (ids.Length == 0 || fraction <= 0.0)
        {
            return;
        }
        SpawnInBatches(ids.Length, label, progress, ct, (start, end) =>
        {
            using var tx = engine.CreateQuickTransaction();
            for (int i = start; i < end; i++)
            {
                if (rand.NextDouble() < fraction)
                {
                    var e = tx.OpenMut(ids[i]);
                    e.Disable(comp);
                }
            }
            if (!tx.Commit()) throw new InvalidOperationException($"{label} commit failed");
        });
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
        // The bulk path trades fidelity for throughput: ComponentCollections are left EMPTY (the CC accessor is
        // Transaction-only), and the enable/disable + cascade-delete passes are SKIPPED (the bulk session has no
        // per-entity Open/mutate semantics). It produces a feature-shaped but data-static snapshot for
        // multi-million-entity volume / File-Map / index stress. Use the regular Populate path for feature-complete
        // fixtures (CC contents, disabled fractions, cascade deletes).
        var rand = new Random(cfg.Seed);
        var resourceTypeIds = new EntityId[cfg.ResourceTypeCount];
        var rtTiers = new int[cfg.ResourceTypeCount];
        var guildIds = new EntityId[cfg.GuildCount];
        var recipeIds = new EntityId[cfg.RecipeCount];
        var playerIds = new EntityId[cfg.PlayerCount];
        var depositIds = new EntityId[cfg.DepositCount];
        int professions = Math.Max(1, cfg.ProfessionCount);

        progress?.Report(new FixtureProgressReport("Opening bulk session", 0, 0));
        using var bulk = engine.BeginBulkLoad();

        RunBulkPhase(cfg.ResourceTypeCount, "Spawning ResourceTypes (bulk)", progress, ct, i =>
        {
            var parent = EntityLink<ResourceTypeArch>.Null;
            int tier = 0;
            if (i > 0 && cfg.ResourceTaxonomyDepth > 1 && rand.NextDouble() > 0.30)
            {
                int p = rand.Next(0, i);
                parent = resourceTypeIds[p];
                tier = Math.Min(rtTiers[p] + 1, cfg.ResourceTaxonomyDepth - 1);
            }
            rtTiers[i] = tier;
            var rt = new ResourceType { Name = S64($"Res-{i}"), Tier = tier, Parent = parent };
            resourceTypeIds[i] = bulk.Spawn<ResourceTypeArch>(ResourceTypeArch.ResourceType.Set(in rt));
        });

        RunBulkPhase(cfg.GuildCount, "Spawning Guilds (bulk)", progress, ct, i =>
        {
            var g = new Guild { Name = S64($"Guild-{i}"), Faction = rand.Next(0, 4), MemberCount = rand.Next(0, 500), Treasury = rand.Next() };
            guildIds[i] = bulk.Spawn<GuildArch>(GuildArch.Guild.Set(in g));
        });

        RunBulkPhase(cfg.RecipeCount, "Spawning Recipes (bulk)", progress, ct, i =>
        {
            var r = new Recipe
            {
                Name = S64($"Recipe-{i}"), Tier = rand.Next(0, 5),
                ProfessionReq = rand.Next(0, professions), PrimaryClass = PickLink<ResourceTypeArch>(resourceTypeIds, rand),
            };
            recipeIds[i] = bulk.Spawn<RecipeArch>(RecipeArch.Recipe.Set(in r));
        });

        RunBulkPhase(cfg.PlayerCount, "Spawning Players (bulk)", progress, ct, i =>
        {
            var p = new Player
            {
                Name = S64($"Player-{i}"), AccountId = i, Level = rand.Next(1, 91),
                ProfessionId = rand.Next(0, professions), CreatedAt = i,
            };
            var m = new Membership { Guild = PickGuild(guildIds, cfg.PlayersPerGuildShape, rand), GuildRank = rand.Next(0, 6) };
            var w = new Wallet { Credits = rand.Next(0, 1_000_000), BankCredits = rand.Next(0, 100_000_000) };
            var pos = new PlayerPosition { Bounds = PlaceAabb(rand) };
            var sess = new Session { ConnectionId = i, LatencyMs = rand.Next(5, 300) };
            playerIds[i] = bulk.Spawn<PlayerArch>(
                PlayerArch.Player.Set(in p), PlayerArch.Membership.Set(in m), PlayerArch.Wallet.Set(in w),
                PlayerArch.Position.Set(in pos), PlayerArch.Session.Set(in sess));
        });

        RunBulkPhase(cfg.DepositCount, "Spawning ResourceDeposits (bulk)", progress, ct, i =>
        {
            var d = new Deposit
            {
                Type = PickLink<ResourceTypeArch>(resourceTypeIds, rand),
                Quality = rand.Next(0, 100), Concentration = rand.Next(0, 100), DepletesAt = rand.Next(),
            };
            var pos = new DepositPosition { Bounds = PlaceAabb(rand) };
            depositIds[i] = bulk.Spawn<ResourceDepositArch>(ResourceDepositArch.Deposit.Set(in d), ResourceDepositArch.Position.Set(in pos));
        });

        RunBulkPhase(cfg.HarvesterCount, "Spawning Harvesters (bulk)", progress, ct, i =>
        {
            var st = new Structure { TypeCode = rand.Next(0, 10), PlacedAt = i, Maintenance = rand.Next(0, 100) };
            var owner = new StructureOwner { Owner = PickLink<PlayerArch>(playerIds, rand) };
            var hop = new Hopper { Class = PickLink<ResourceTypeArch>(resourceTypeIds, rand), Amount = rand.Next(0, 1000), Rate = rand.Next(1, 50) };
            var tgt = new HarvesterTarget { Deposit = PickLink<ResourceDepositArch>(depositIds, rand) };
            var maint = new MaintenanceState { PaidUntil = rand.Next() };
            var pos = new StructurePosition { Bounds = PlaceAabb(rand) };
            bulk.Spawn<HarvesterArch>(
                StructureArch.Structure.Set(in st), StructureArch.Owner.Set(in owner), HarvesterArch.Hopper.Set(in hop),
                HarvesterArch.Target.Set(in tgt), HarvesterArch.Maintenance.Set(in maint), HarvesterArch.Position.Set(in pos));
        });

        if (cfg.IncludePolymorphicStructure)
        {
            RunBulkPhase(cfg.FactoryCount, "Spawning Factories (bulk)", progress, ct, i =>
            {
                var st = new Structure { TypeCode = rand.Next(0, 10), PlacedAt = i, Maintenance = rand.Next(0, 100) };
                var owner = new StructureOwner { Owner = PickLink<PlayerArch>(playerIds, rand) };
                var fc = new FactoryConfig { Recipe = PickLink<RecipeArch>(recipeIds, rand), RemainingRuns = rand.Next(0, 1000) };
                var pw = new PowerSupply { CreditsRemaining = rand.Next() };
                var pos = new StructurePosition { Bounds = PlaceAabb(rand) };
                bulk.Spawn<FactoryArch>(
                    StructureArch.Structure.Set(in st), StructureArch.Owner.Set(in owner), FactoryArch.Config.Set(in fc),
                    FactoryArch.Power.Set(in pw), FactoryArch.Position.Set(in pos));
            });
        }

        RunBulkPhase(cfg.ItemCount, "Spawning Items (bulk)", progress, ct, i =>
        {
            var it = new Item
            {
                Recipe = PickLink<RecipeArch>(recipeIds, rand),
                ItemType = rand.Next(0, 50), Quality = rand.Next(0, 100), Decay = rand.Next(0, 100),
            };
            var owner = new ItemOwner { Owner = PickLink<PlayerArch>(playerIds, rand) };
            bulk.Spawn<ItemArch>(ItemArch.Item.Set(in it), ItemArch.Owner.Set(in owner));
        });

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
        // No wal/ creation — the engine derives {bundle}/wal inside the .typhon bundle it creates under this directory.
    }

    private static ServiceProvider BuildEngineServices(string directory, string databaseName)
    {
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
                // 131072 pages × 8 KB = 1 GiB page cache — matches the Workbench's own open path
                // (EngineLifecycle.cs:116). The 512 MiB the fixture previously ran with worked for the Stress preset
                // (420k) but was too small for multi-million-entity scales: the EntityMap (paged hash) alone needs
                // thousands of pages hot at that count, and adding the component clusters + BTree index pages
                // saturates the cache during Commit (RawValuePagedHashMap.InsertNew → ChunkAccessor.EvictSlot
                // back-pressure). 1 GiB gives multi-million-entity fixtures comfortable headroom on a dev box.
                opts.DatabaseCacheSize = 131072UL * 8192;
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
                    // WalDirectory left null — the engine derives {bundle}/wal inside the .typhon bundle.
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

}

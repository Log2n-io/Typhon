using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Engine;

namespace Typhon.Workbench.Fixtures;

/// <summary>
/// Result of a call to <see cref="FixtureDatabase.CreateOrReuse"/>. Communicates the path of the
/// generated <c>.typhon</c> marker, the accompanying schema DLL (copied alongside for the
/// Workbench's convention loader), and whether this call actually produced new content.
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
/// Workbench dev-fixture database generator. Populates a known set of archetypes (<c>CompAArch</c>
/// … <c>PlayerArch</c>) with deterministic random entity data so the Workbench has real content to
/// browse while we iterate on its UI.
///
/// The method is <c>internal</c> to keep it out of the public schema-loader surface — a user
/// schema DLL shipped to the Workbench should never offer to bulk-populate a database. The engine
/// test project and the Workbench itself have <c>InternalsVisibleTo</c> access.
///
/// Extend <see cref="Populate"/> over time with more archetype shapes, edge-case indexes, etc. —
/// every callsite reuses it, and the "force" flag lets us regenerate on demand without deleting
/// the directory by hand.
/// </summary>
internal static class FixtureDatabase
{
    public const string DefaultDatabaseName = "base-tests";

    private const int CompAArchCount   = 1_000;
    private const int CompABArchCount  = 500;
    private const int CompABCArchCount = 500;
    private const int CompDArchCount   = 200;
    private const int GuildArchCount   = 50;
    private const int PlayerArchCount  = 300;
    private const int ParticleArchCount = 2_000;

    /// <summary>
    /// Ensure the Workbench test database exists under <paramref name="outputDir"/>. When
    /// <paramref name="force"/> is <c>true</c>, the directory is wiped and the database is recreated
    /// even if a previous run left one there. When <c>false</c> and a <c>.typhon</c> + matching
    /// <c>.bin</c> already exist, they are reused as-is and no entities are spawned.
    ///
    /// On a creation run: services are built, archetypes are touched, components are registered,
    /// entities are spawned, a checkpoint is forced, and the engine is disposed (which triggers
    /// <c>PersistArchetypeState</c> — critical for EntityMap counts to survive the reopen).
    /// </summary>
    internal static FixtureGenerationResult CreateOrReuse(string outputDir, bool force)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputDir);

        var absOut = Path.GetFullPath(outputDir);
        var typhonPath = Path.Combine(absOut, $"{DefaultDatabaseName}.typhon");
        var binPath = Path.Combine(absOut, $"{DefaultDatabaseName}.bin");
        var schemaDllPath = Path.Combine(absOut, "Typhon.Workbench.Fixtures.schema.dll");

        var databaseExists = File.Exists(typhonPath) && File.Exists(binPath);
        if (databaseExists && !force)
        {
            return new FixtureGenerationResult(typhonPath, schemaDllPath, TotalEntities: 0, WasCreated: false);
        }

        PrepareOutputDirectory(absOut);

        Archetype<CompAArch>.Touch();
        Archetype<CompABArch>.Touch();
        Archetype<CompABCArch>.Touch();
        Archetype<CompDArch>.Touch();
        Archetype<GuildArch>.Touch();
        Archetype<PlayerArch>.Touch();
        Archetype<ParticleArch>.Touch();

        using (var sp = BuildEngineServices(absOut, DefaultDatabaseName))
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

            Populate(engine);

            // Force a checkpoint so WAL records are applied to the data file before dispose runs
            // PersistArchetypeState / PersistEngineState. Without this, the reopen path could find
            // stale EntityMapSPI values.
            engine.ForceCheckpoint();
        }

        WriteTyphonMarker(absOut);
        CopySchemaDll(absOut);

        int total = CompAArchCount + CompABArchCount + CompABCArchCount
                    + CompDArchCount + GuildArchCount + PlayerArchCount + ParticleArchCount;
        return new FixtureGenerationResult(typhonPath, schemaDllPath, total, WasCreated: true);
    }

    private static void Populate(DatabaseEngine engine)
    {
        var rand = new Random(123456789);

        var guildIds = new long[GuildArchCount];
        using (var tx = engine.CreateQuickTransaction())
        {
            for (int i = 0; i < GuildArchCount; i++)
            {
                var g = new CompGuild { Level = rand.Next(1, 60), MemberCap = 100 + i };
                var eid = tx.Spawn<GuildArch>(GuildArch.Guild.Set(in g));
                guildIds[i] = eid.EntityKey;
            }
            if (!tx.Commit())
            {
                throw new InvalidOperationException("Guild batch commit failed");
            }
        }

        using (var tx = engine.CreateQuickTransaction())
        {
            for (int i = 0; i < CompAArchCount; i++)
            {
                var a = new CompA(rand.Next(), (float)rand.NextDouble(), rand.NextDouble());
                tx.Spawn<CompAArch>(CompAArch.A.Set(in a));
            }
            for (int i = 0; i < CompABArchCount; i++)
            {
                var a = new CompA(rand.Next(), (float)rand.NextDouble(), rand.NextDouble());
                var b = new CompB(rand.Next(), (float)rand.NextDouble());
                tx.Spawn<CompABArch>(CompABArch.A.Set(in a), CompABArch.B.Set(in b));
            }
            for (int i = 0; i < CompABCArchCount; i++)
            {
                var a = new CompA(rand.Next(), (float)rand.NextDouble(), rand.NextDouble());
                var b = new CompB(rand.Next(), (float)rand.NextDouble());
                var c = new CompC($"entity-{i:D4}");
                tx.Spawn<CompABCArch>(CompABCArch.A.Set(in a), CompABCArch.B.Set(in b), CompABCArch.C.Set(in c));
            }
            for (int i = 0; i < CompDArchCount; i++)
            {
                var d = new CompD { Weight = (float)rand.NextDouble() * 1000f, Key = i, Raw = rand.NextDouble() };
                tx.Spawn<CompDArch>(CompDArch.D.Set(in d));
            }
            for (int i = 0; i < PlayerArchCount; i++)
            {
                var guildKey = guildIds[rand.Next(0, guildIds.Length)];
                var p = new CompPlayer { GuildId = guildKey, Active = rand.Next(0, 4) != 0 ? 1 : 0 };
                tx.Spawn<PlayerArch>(PlayerArch.Player.Set(in p));
            }
            if (!tx.Commit())
            {
                throw new InvalidOperationException("Bulk commit failed");
            }
        }

        // Cluster-eligible (SingleVersion) archetype — spawn a block, then destroy a scattered ~40% so the
        // clusters end up partially filled. That gives the Database File Map's A6 L3 rendering a real spread of
        // intra-cluster fill (the half-empty-cluster fragmentation signal), not a wall of full clusters.
        var particleIds = new EntityId[ParticleArchCount];
        using (var tx = engine.CreateQuickTransaction())
        {
            for (int i = 0; i < ParticleArchCount; i++)
            {
                var p = new CompParticle((float)rand.NextDouble(), (float)rand.NextDouble(), (float)rand.NextDouble());
                particleIds[i] = tx.Spawn<ParticleArch>(ParticleArch.Particle.Set(in p));
            }
            if (!tx.Commit())
            {
                throw new InvalidOperationException("Particle spawn commit failed");
            }
        }

        using (var tx = engine.CreateQuickTransaction())
        {
            for (int i = 0; i < ParticleArchCount; i++)
            {
                if (rand.Next(0, 5) < 2)
                {
                    tx.Destroy(particleIds[i]);
                }
            }
            if (!tx.Commit())
            {
                throw new InvalidOperationException("Particle destroy commit failed");
            }
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
                opts.DatabaseCacheSize = 8192UL * 8192;
                opts.PagesDebugPattern = false;
            })
            .AddScopedDatabaseEngine(_ => { });
        return services.BuildServiceProvider();
    }

    private static void WriteTyphonMarker(string outDir)
    {
        var marker = Path.Combine(outDir, $"{DefaultDatabaseName}.typhon");
        if (!File.Exists(marker))
        {
            File.WriteAllText(marker, string.Empty);
        }
    }

    /// <summary>
    /// Copy the fixture schema DLL (and a defensive set of engine-side dependencies) next to the
    /// generated database so the Workbench's schema-convention loader finds them without the user
    /// having to paste any path. The engine DLLs are resolved from this assembly's base directory —
    /// both the test process and the Workbench process publish them to their bin output.
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

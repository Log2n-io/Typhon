using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Serilog;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component(SchemaName, 1)]
[StructLayout(LayoutKind.Sequential)]
[PublicAPI]
public struct CompA
{
    private const string SchemaName = "Typhon.Schema.UnitTest.CompA";
    public int A;
    public float B;
    public double C;

    public static CompA Create(Random rand) => new() { A = rand.Next(), B = (float)rand.NextDouble(), C = rand.NextDouble() };

    public CompA(int a, float b=1.234f, double c=5.678)
    {
        A = a;
        B = b;
        C = c;
    }
    
    public void Update(Random rand)
    {
        A = rand.Next();
        B = (float)rand.NextDouble();
        C = rand.NextDouble();
    }

    public override string ToString() => $"A={A}, B={B}, C={C}";
}

[Component(SchemaName, 1)]
[StructLayout(LayoutKind.Sequential)]
public struct CompB
{
    private const string SchemaName = "Typhon.Schema.UnitTest.CompB";
    public int A;
    public float B;

    public CompB(int a, float b)
    {
        A = a;
        B = b;
    }
}

[Component(SchemaName, 1)]
[StructLayout(LayoutKind.Sequential)]
public struct CompC
{
    private const string SchemaName = "Typhon.Schema.UnitTest.CompC";
    public String64 String;

    public CompC(string str)
    {
        String.AsString = str;
    }
}

[Component(SchemaName, 1)]
[StructLayout(LayoutKind.Sequential)]
public struct CompD
{
    private const string SchemaName = "Typhon.Schema.UnitTest.CompD";

    [Index(AllowMultiple = true)]
    public float A;
    [Index]
    public int B;
    [Index(AllowMultiple = true)]
    public double C;

    public CompD(float a, int b, double c)
    {
        A = a;
        B = b;
        C = c;
    }
}


[Component(SchemaName, 1)]
[StructLayout(LayoutKind.Sequential)]
public struct CompF
{
    private const string SchemaName = "Typhon.Schema.UnitTest.CompF";

    [Index(AllowMultiple = true)]
    public int Gold;
    [Index]
    public int Rank;

    public CompF(int gold, int rank)
    {
        Gold = gold;
        Rank = rank;
    }
}

// ── Shared test component types (moved from deleted NavigationViewTests.cs) ──

[Component("Typhon.Schema.UnitTest.TestGuild", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct CompGuild
{
    [Index(AllowMultiple = true)] public int Level;
    [Index] public int MemberCap;

    public CompGuild(int level, int memberCap)
    {
        Level = level;
        MemberCap = memberCap;
    }
}

[Component("Typhon.Schema.UnitTest.TestPlayer", 1)]
[StructLayout(LayoutKind.Sequential)]
public struct CompPlayer
{
    [Index(AllowMultiple = true), ForeignKey(typeof(CompGuild))]
    public long GuildId;
    [Index(AllowMultiple = true)] public int Active;

    public CompPlayer(long guildId, bool active)
    {
        GuildId = guildId;
        Active = active ? 1 : 0;
    }
}

// ── Shared test archetypes for ECS migration ──
// IDs 200+ to avoid collisions with ECS test archetypes (EcsUnit=100, EcsSoldier=101, SvTestArchetype=50, etc.)

[Archetype(200)]
class CompAArch : Archetype<CompAArch>
{
    public static readonly Comp<CompA> A = Register<CompA>();
}

[Archetype(201)]
class CompDArch : Archetype<CompDArch>
{
    public static readonly Comp<CompD> D = Register<CompD>();
}

[Archetype(202)]
class CompAEArch : Archetype<CompAEArch>
{
    public static readonly Comp<CompA> A = Register<CompA>();
    public static readonly Comp<CompE_Eng> E = Register<CompE_Eng>();
}

[Archetype(203)]
class CompABCArch : Archetype<CompABCArch>
{
    public static readonly Comp<CompA> A = Register<CompA>();
    public static readonly Comp<CompB> B = Register<CompB>();
    public static readonly Comp<CompC> C = Register<CompC>();
}

[Archetype(204)]
class CompABArch : Archetype<CompABArch>
{
    public static readonly Comp<CompA> A = Register<CompA>();
    public static readonly Comp<CompB> B = Register<CompB>();
}

[Archetype(205)]
class CompBArch : Archetype<CompBArch>
{
    public static readonly Comp<CompB> B = Register<CompB>();
}

[Archetype(206)]
class CompABDArch : Archetype<CompABDArch>
{
    public static readonly Comp<CompA> A = Register<CompA>();
    public static readonly Comp<CompB> B = Register<CompB>();
    public static readonly Comp<CompD> D = Register<CompD>();
}

[Archetype(207)]
class CompFArch : Archetype<CompFArch>
{
    public static readonly Comp<CompF> F = Register<CompF>();
}

[Archetype(208)]
class CompDFArch : Archetype<CompDFArch>
{
    public static readonly Comp<CompD> D = Register<CompD>();
    public static readonly Comp<CompF> F = Register<CompF>();
}

[Archetype(209)]
class CompGuildArch : Archetype<CompGuildArch>
{
    public static readonly Comp<CompGuild> Guild = Register<CompGuild>();
}

[Archetype(210)]
class CompPlayerArch : Archetype<CompPlayerArch>
{
    public static readonly Comp<CompPlayer> Player = Register<CompPlayer>();
}

[PublicAPI]
public abstract class TestBase{
    protected readonly Random Rand;
    private static readonly char[] CharToRemove = ['(', ')', ','];
    private static readonly (string, string)[] WordsToReplace = [("true", "t"), ("false", "f")];
    public virtual bool UseSeq => false;
    public virtual Action<LoggerConfiguration> ExtraLoggerConf => null;
    protected static string CurrentDatabaseName
    {
        get
        {
            var testName = TestContext.CurrentContext.Test.Name;

            foreach (var c in CharToRemove)
            {
                testName = testName.Replace(c, '_');
            }
            foreach ((string oldWord, string newWord) in WordsToReplace)
            {
                testName = testName.Replace(oldWord, newWord);
            }
            var databaseName = $"T_{testName}_db";
            if (Encoding.UTF8.GetByteCount(databaseName) > PagedMMFOptions.DatabaseNameMaxUtf8Size)
            {
                databaseName = $"T_{testName.Substring(testName.Length - (PagedMMFOptions.DatabaseNameMaxUtf8Size - 5))}_db";
            }
            return databaseName;
        }
    }

    protected TestBase()
    {
        Rand = new Random(123456789);
    }

    protected static System.Collections.IEnumerable BuildNoiseCasesL1(int maxNoiseMode = 2)
    {
        for (int noiseMode = 0; noiseMode <= maxNoiseMode; noiseMode++)
        {
            foreach (bool l1 in (bool[])[true, false])
            {
                yield return new object[] { noiseMode, l1};
            }
        }
    }
    
    protected static System.Collections.IEnumerable BuildNoiseCasesL2(int maxNoiseMode = 2)
    {
        for (int noiseMode = 0; noiseMode <= maxNoiseMode; noiseMode++)
        {
            foreach (bool l1 in (bool[])[true, false])
            {
                foreach (bool l2 in (bool[])[true, false])
                {
                    yield return new object[] { noiseMode, l1, l2 };
                }
            }
        }
    }    
    protected virtual void RegisterComponents(DatabaseEngine dbe)
    {
        dbe.RegisterComponentFromAccessor<CompA>();
        dbe.RegisterComponentFromAccessor<CompB>();
        dbe.RegisterComponentFromAccessor<CompC>();
        dbe.RegisterComponentFromAccessor<CompD>();
        dbe.RegisterComponentFromAccessor<CompF>();
    }
    
    protected EntityId[] CreateNoiseCompA(DatabaseEngine dbe, Transaction t = null, int count = 10)
    {
        RegisterComponents(dbe);

        var cur = t ?? dbe.CreateQuickTransaction();

        var res = new EntityId[count];
        for (int i = 0; i < count; i++)
        {
            var a = CompA.Create(Rand);
            res[i] = cur.Spawn<CompAArch>(CompAArch.A.Set(in a));
        }

        if (t == null)
        {
            cur.Commit();
            cur.Dispose();
        }

        return res;
    }

    protected void UpdateNoiseCompA(DatabaseEngine dbe, Transaction t, EntityId[] ids)
    {
        RegisterComponents(dbe);

        var cur = t ?? dbe.CreateQuickTransaction();

        for (var i = 0; i < ids.Length; i++)
        {
            CompA a = default;
            if ((i & 1) != 0)
            {
                a = cur.Open(ids[i]).Read(CompAArch.A);
            }

            a.Update(Rand);
            ref var w = ref cur.OpenMut(ids[i]).Write(CompAArch.A);
            w = a;
        }

        if (t == null)
        {
            cur.Commit();
            cur.Dispose();
        }
    }

    protected void ReadNoiseCompA(DatabaseEngine dbe, Transaction t, EntityId[] ids)
    {
        RegisterComponents(dbe);

        var cur = t ?? dbe.CreateQuickTransaction();

        for (var i = 0; i < ids.Length; i++)
        {
            cur.Open(ids[i]).Read(CompAArch.A);
        }

        if (t == null)
        {
            cur.Dispose();
        }
    }
}


[PublicAPI]
abstract class TestBase<T> : TestBase
{
    protected IServiceProvider ServiceProvider;
    protected ServiceCollection ServiceCollection;
    protected ILogger<T> Logger;

    // Convenience accessors for DI-provided resources
    protected IResourceRegistry ResourceRegistry => ServiceProvider.GetRequiredService<IResourceRegistry>();
    protected IMemoryAllocator MemoryAllocator => ServiceProvider.GetRequiredService<IMemoryAllocator>();
    protected IResource AllocationResource => ResourceRegistry.Allocation;

    // Per-fixture temp directory to avoid NTFS MFT contention when 8 fixtures run in parallel.
    // Each fixture gets its own subdirectory under the system temp path, isolated from other fixtures.
    private string _testDatabaseDir;

    [SetUp]
    public virtual void Setup()
    {
        var o = TestContext.CurrentContext.Test.Properties.ContainsKey("CacheSize");
        var dcs = o ? (int)TestContext.CurrentContext.Test.Properties.Get("CacheSize")! : (int)PagedMMF.MinimumCacheSize;

        // Create a per-fixture temp directory to spread I/O across directories
        _testDatabaseDir = Path.Combine(Path.GetTempPath(), "Typhon.Tests", typeof(T).Name);
        Directory.CreateDirectory(_testDatabaseDir);

        var serviceCollection = new ServiceCollection();
        ServiceCollection = serviceCollection;
        ServiceCollection
            .AddLogging(builder =>
            {
                builder.AddSerilog();
                builder.AddSimpleConsole(options =>
                {
                    options.SingleLine = true;
                    options.IncludeScopes = true;
                    options.TimestampFormat = "mm:ss.fff ";
                });
                builder.SetMinimumLevel(LogLevel.Information);
            })
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = CurrentDatabaseName;
                options.DatabaseDirectory = _testDatabaseDir;
                options.DatabaseCacheSize = (ulong)dcs;
                options.TestMode = true; // tests run a deliberately small cache: allow sub-2MiB and suppress the production small-cache warning
                options.PagesDebugPattern = false;
            })
            .AddScopedDatabaseEngine(ConfigureEngineOptions);

        // WAL is mandatory — every test engine runs the real WAL + checkpoint pipeline. Route it through an in-memory file-IO backend (scoped: a fresh
        // instance per DI scope, so reopen tests that CreateScope() twice each get their own and there is no cross-scope dispose hazard) for zero disk I/O.
        // Crash/replay tests that need WAL segments to survive a reopen override CreateWalFileIO() to return a disk-backed WalFileIO.
        ServiceCollection.AddScoped<IWalFileIO>(_ => CreateWalFileIO());

        ServiceProvider = ServiceCollection.BuildServiceProvider();
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        Logger = ServiceProvider.GetRequiredService<ILogger<T>>();
    }

    /// <summary>
    /// WAL file-IO backend for the test engine. Default is an <see cref="InMemoryWalFileIO"/> (no disk I/O). Override to return <c>new WalFileIO()</c> for
    /// crash/replay fixtures that need WAL segments to survive an engine reopen within the same fixture.
    /// </summary>
    protected virtual IWalFileIO CreateWalFileIO() => new InMemoryWalFileIO();

    /// <summary>
    /// Configures the test engine's durability options. Default applies the fast test profile (in-memory WAL, FUA off, aggressive checkpoint so the
    /// page-cache DirtyCounter drains). Override to tune for a specific fixture (e.g. a larger WAL ring or a different checkpoint cadence).
    /// </summary>
    protected virtual void ConfigureEngineOptions(DatabaseEngineOptions o) => TestWalProfile.Apply(o, _testDatabaseDir);

    [TearDown]
    public virtual void TearDown()
    {
        (ServiceProvider as IDisposable)?.Dispose();
        Log.CloseAndFlush();

        // Clean up the database file after each test to prevent accumulation
        CleanupDatabaseFile();
    }

    private void CleanupDatabaseFile()
    {
        if (_testDatabaseDir == null)
        {
            return;
        }

        try
        {
            // A database is a {name}.typhon bundle directory (data + db.lock inside) — remove the whole bundle.
            var bundle = Path.Combine(_testDatabaseDir, $"{CurrentDatabaseName}.typhon");
            if (Directory.Exists(bundle))
            {
                Directory.Delete(bundle, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup — don't fail the test if deletion fails
        }
    }
}
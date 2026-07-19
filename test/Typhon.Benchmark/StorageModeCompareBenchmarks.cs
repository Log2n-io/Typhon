using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════
// Components — identical layout, different storage modes
// ═══════════════════════════════════════════════════════════════════════

[Component("Typhon.Benchmark.SM.Versioned", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
public struct SmVersioned
{
    [Field] public int Value;
    [Field] public long Timestamp;
}

[Component("Typhon.Benchmark.SM.SingleVersion", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct SmSingleVersion
{
    [Field] public int Value;
    [Field] public long Timestamp;
}

[Component("Typhon.Benchmark.SM.Transient", 1, StorageMode = StorageMode.Transient)]
[StructLayout(LayoutKind.Sequential)]
public struct SmTransient
{
    [Field] public int Value;
    [Field] public long Timestamp;
}

// Indexed variants for query benchmarks
[Component("Typhon.Benchmark.SM.SvIndexed", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
public struct SmSvIndexed
{
    [Index(AllowMultiple = true)]
    public int Category;
    [Field] public int Value;
}

[Component("Typhon.Benchmark.SM.TransientIndexed", 1, StorageMode = StorageMode.Transient)]
[StructLayout(LayoutKind.Sequential)]
public struct SmTransientIndexed
{
    [Index(AllowMultiple = true)]
    public int Category;
    [Field] public int Value;
}

[Component("Typhon.Benchmark.SM.VersionedIndexed", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
public struct SmVersionedIndexed
{
    [Index(AllowMultiple = true)]
    public int Category;
    [Field] public int Value;
}

// ═══════════════════════════════════════════════════════════════════════
// Archetypes (IDs 520-525)
// ═══════════════════════════════════════════════════════════════════════

[Archetype(520)] class SmVersionedArch : Archetype<SmVersionedArch> { public static readonly Comp<SmVersioned> Data = Register<SmVersioned>(); }
[Archetype(521)] class SmSingleVersionArch : Archetype<SmSingleVersionArch> { public static readonly Comp<SmSingleVersion> Data = Register<SmSingleVersion>(); }
[Archetype(522)] class SmTransientArch : Archetype<SmTransientArch> { public static readonly Comp<SmTransient> Data = Register<SmTransient>(); }
[Archetype(523)] class SmVersionedIdxArch : Archetype<SmVersionedIdxArch> { public static readonly Comp<SmVersionedIndexed> Data = Register<SmVersionedIndexed>(); }
[Archetype(524)] class SmSvIdxArch : Archetype<SmSvIdxArch> { public static readonly Comp<SmSvIndexed> Data = Register<SmSvIndexed>(); }
[Archetype(525)] class SmTransientIdxArch : Archetype<SmTransientIdxArch> { public static readonly Comp<SmTransientIndexed> Data = Register<SmTransientIndexed>(); }

// ═══════════════════════════════════════════════════════════════════════
// Stopwatch-based Storage Mode Profiler
// Simple loop/time approach: warmup then measure for >= 200ms per test.
// ═══════════════════════════════════════════════════════════════════════

public class StorageModeCompareBenchmarks : IDisposable
{
    private ServiceProvider _serviceProvider;
    private DatabaseEngine _dbe;
    private string _databaseName;

    private const int EntityCount = 1000;
    private EntityId[] _vIds, _svIds, _tIds;
    private EntityId[] _viIds, _sviIds, _tiIds;

    public void Setup()
    {
        _databaseName = $"SmCompare_{Environment.ProcessId}";


        var dcs = (ulong)(200 * 1024 * PagedMMF.PageSize);

        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry()
          .AddMemoryAllocator()
          .AddEpochManager()
          .AddHighResolutionSharedTimer()
          .AddDeadlineWatchdog()
          .AddScopedManagedPagedMemoryMappedFile(options =>
          {
              options.DatabaseName = _databaseName;
              options.DatabaseCacheSize = dcs;
              options.PagesDebugPattern = false;
          })
          .AddScopedDatabaseEngine();

        _serviceProvider = sc.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _dbe = _serviceProvider.GetRequiredService<DatabaseEngine>();

        _dbe.RegisterComponentFromAccessor<SmVersioned>();
        _dbe.RegisterComponentFromAccessor<SmSingleVersion>();
        _dbe.RegisterComponentFromAccessor<SmTransient>();
        _dbe.RegisterComponentFromAccessor<SmVersionedIndexed>();
        _dbe.RegisterComponentFromAccessor<SmSvIndexed>();
        _dbe.RegisterComponentFromAccessor<SmTransientIndexed>();
        _dbe.InitializeArchetypes();

        // Pre-grow EntityMap
        var pg = new EntityId[200_000];
        using (var gt = _dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < pg.Length; i++)
            {
                var c = new SmVersioned { Value = i, Timestamp = 12345 };
                pg[i] = gt.Spawn<SmVersionedArch>(SmVersionedArch.Data.Set(in c));
            }
            gt.Commit();
        }
        using (var dt = _dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < pg.Length; i++)
            {
                dt.Destroy(pg[i]);
            }
            dt.Commit();
        }
        _dbe.FlushDeferredCleanups();

        // Pre-populate plain entities
        _vIds = new EntityId[EntityCount];
        _svIds = new EntityId[EntityCount];
        _tIds = new EntityId[EntityCount];
        using (var t = _dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < EntityCount; i++)
            {
                var v = new SmVersioned { Value = i, Timestamp = 12345 };
                _vIds[i] = t.Spawn<SmVersionedArch>(SmVersionedArch.Data.Set(in v));
                var sv = new SmSingleVersion { Value = i, Timestamp = 12345 };
                _svIds[i] = t.Spawn<SmSingleVersionArch>(SmSingleVersionArch.Data.Set(in sv));
                var tr = new SmTransient { Value = i, Timestamp = 12345 };
                _tIds[i] = t.Spawn<SmTransientArch>(SmTransientArch.Data.Set(in tr));
            }
            t.Commit();
        }

        // Pre-populate indexed entities
        _viIds = new EntityId[EntityCount];
        _sviIds = new EntityId[EntityCount];
        _tiIds = new EntityId[EntityCount];
        using (var t = _dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < EntityCount; i++)
            {
                var vi = new SmVersionedIndexed { Category = i % 10, Value = i };
                _viIds[i] = t.Spawn<SmVersionedIdxArch>(SmVersionedIdxArch.Data.Set(in vi));
                var si = new SmSvIndexed { Category = i % 10, Value = i };
                _sviIds[i] = t.Spawn<SmSvIdxArch>(SmSvIdxArch.Data.Set(in si));
                var ti = new SmTransientIndexed { Category = i % 10, Value = i };
                _tiIds[i] = t.Spawn<SmTransientIdxArch>(SmTransientIdxArch.Data.Set(in ti));
            }
            t.Commit();
        }
        _dbe.WriteTickFence(1);
    }

    public void Dispose()
    {
        _dbe?.Dispose();
        _serviceProvider?.Dispose();
        try { File.Delete($"{_databaseName}.bin"); } catch { }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Measurement harness
    // ═══════════════════════════════════════════════════════════════════

    private const long MinDurationMs = 200;
    private const int WarmupIterations = 500;

    private static (long iterations, double nsPerOp) Measure(Action action)
    {
        // Warmup
        for (int i = 0; i < WarmupIterations; i++)
        {
            action();
        }

        // Measure: run until at least MinDurationMs elapsed
        long iterations = 0;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < MinDurationMs)
        {
            action();
            iterations++;
        }
        sw.Stop();

        double nsPerOp = (double)sw.ElapsedTicks / Stopwatch.Frequency * 1_000_000_000.0 / iterations;
        return (iterations, nsPerOp);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Benchmark operations
    // ═══════════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ReadBatch_V()
    {
        using var t = _dbe.CreateQuickTransaction();
        for (int i = 0; i < 100; i++) { _ = t.Open(_vIds[i]).Read(SmVersionedArch.Data); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ReadBatch_SV()
    {
        using var t = _dbe.CreateQuickTransaction();
        for (int i = 0; i < 100; i++) { _ = t.Open(_svIds[i]).Read(SmSingleVersionArch.Data); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ReadBatch_T()
    {
        using var t = _dbe.CreateQuickTransaction();
        for (int i = 0; i < 100; i++) { _ = t.Open(_tIds[i]).Read(SmTransientArch.Data); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void WriteBatch_V()
    {
        using var t = _dbe.CreateQuickTransaction();
        for (int i = 0; i < 100; i++) { t.OpenMut(_vIds[i]).Write(SmVersionedArch.Data).Value = i; }
        t.Commit();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void WriteBatch_SV()
    {
        using var t = _dbe.CreateQuickTransaction();
        for (int i = 0; i < 100; i++) { t.OpenMut(_svIds[i]).Write(SmSingleVersionArch.Data).Value = i; }
        t.Commit();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void WriteBatch_T()
    {
        using var t = _dbe.CreateQuickTransaction();
        for (int i = 0; i < 100; i++) { t.OpenMut(_tIds[i]).Write(SmTransientArch.Data).Value = i; }
        t.Commit();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void SpawnDestroy_V()
    {
        using var t = _dbe.CreateQuickTransaction();
        var c = new SmVersioned { Value = 42, Timestamp = 12345 };
        var id = t.Spawn<SmVersionedArch>(SmVersionedArch.Data.Set(in c));
        t.Destroy(id);
        t.Commit();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void SpawnDestroy_SV()
    {
        using var t = _dbe.CreateQuickTransaction();
        var c = new SmSingleVersion { Value = 42, Timestamp = 12345 };
        var id = t.Spawn<SmSingleVersionArch>(SmSingleVersionArch.Data.Set(in c));
        t.Destroy(id);
        t.Commit();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void SpawnDestroy_T()
    {
        using var t = _dbe.CreateQuickTransaction();
        var c = new SmTransient { Value = 42, Timestamp = 12345 };
        var id = t.Spawn<SmTransientArch>(SmTransientArch.Data.Set(in c));
        t.Destroy(id);
        t.Commit();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void QueryCount_V()
    {
        using var t = _dbe.CreateQuickTransaction();
        t.Query<SmVersionedIdxArch>().WhereField<SmVersionedIndexed>(d => d.Category == 5).Count();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void QueryCount_SV()
    {
        using var t = _dbe.CreateQuickTransaction();
        t.Query<SmSvIdxArch>().WhereField<SmSvIndexed>(d => d.Category == 5).Count();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void QueryCount_T()
    {
        using var t = _dbe.CreateQuickTransaction();
        t.Query<SmTransientIdxArch>().WhereField<SmTransientIndexed>(d => d.Category == 5).Count();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Runner
    // ═══════════════════════════════════════════════════════════════════

    public void Run()
    {
        Console.WriteLine($"StorageMode Comparison Benchmark (min {MinDurationMs}ms per test, {WarmupIterations} warmup iters)");
        Console.WriteLine($"Ryzen {Environment.ProcessorCount} logical cores, .NET {Environment.Version}");
        Console.WriteLine(new string('─', 90));
        Console.WriteLine($"{"Test",-35} {"Versioned",12} {"SV",12} {"Transient",12}  {"SV/V",7} {"T/V",7}");
        Console.WriteLine(new string('─', 90));

        RunGroup("Read 100 entities",     ReadBatch_V,    ReadBatch_SV,    ReadBatch_T,    100);
        RunGroup("Write 100 entities",    WriteBatch_V,   WriteBatch_SV,   WriteBatch_T,   100);
        RunGroup("Spawn+Destroy (1 ent)", SpawnDestroy_V, SpawnDestroy_SV, SpawnDestroy_T, 1);
        RunGroup("QueryCount (idx, 100)", QueryCount_V,   QueryCount_SV,   QueryCount_T,   1);

        Console.WriteLine(new string('─', 90));
    }

    private static void RunGroup(string name, Action vAction, Action svAction, Action tAction, int opsPerInvoke)
    {
        var (_, vNs)  = Measure(vAction);
        var (_, svNs) = Measure(svAction);
        var (_, tNs)  = Measure(tAction);

        double vPerOp  = vNs  / opsPerInvoke;
        double svPerOp = svNs / opsPerInvoke;
        double tPerOp  = tNs  / opsPerInvoke;

        string vStr  = FormatNs(vPerOp);
        string svStr = FormatNs(svPerOp);
        string tStr  = FormatNs(tPerOp);

        Console.WriteLine($"{name,-35} {vStr,12} {svStr,12} {tStr,12}  {svPerOp / vPerOp,7:0.00}x {tPerOp / vPerOp,7:0.00}x");
    }

    private static string FormatNs(double ns)
    {
        if (ns >= 1_000_000) return $"{ns / 1_000_000:F1} ms";
        if (ns >= 1_000)     return $"{ns / 1_000:F1} us";
        return $"{ns:F0} ns";
    }
}

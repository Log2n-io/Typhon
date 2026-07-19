using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.CompetitiveBenchmark.Adapters;

/// <summary>
/// Typhon under test, three C0 ladder rungs over the SAME hardware/dataset, all at D0 (in-mem WAL, no fsync) so the
/// ladder isolates *CPU* cost of each guarantee (the fsync delta is C4's job):
/// <list type="bullet">
/// <item><b>SvLean</b> — SingleVersion + TickFence: in-place last-writer-wins (I0-lean). Matched to the floor.</item>
/// <item><b>SvCommitted</b> — SingleVersion + Commit discipline (I0-txn): atomic, per-commit, no chain. No MVCC.</item>
/// <item><b>Versioned</b> — MVCC snapshot isolation (I1): copy-on-write revision chain.</item>
/// </list>
/// </summary>
public sealed class TyphonAdapter : IEngineAdapter
{
    public enum Config { SvLean, SvCommitted, Versioned }

    /// <summary>Process-wide toggle (captured at construction): true = FUA power-safe write; false = fsync-to-cache (matches the SQL/KV engines on write-back NVMe).</summary>
    public static bool UseFua = true;

    /// <summary>
    /// When true, commits run in <see cref="DurabilityMode.Deferred"/> even on D2 (on-disk WAL): the Commit-discipline WAL
    /// redo record is written and the tx is logically committed + published, but the commit does NOT block on the fsync
    /// (the background writer flushes async). This measures the transaction-commit cost itself, not the durability wait —
    /// "durable-soon" (group-commit / Postgres synchronous_commit=off), vs Immediate's "durable-at-return" zero-loss.
    /// </summary>
    public static bool ForceDeferredDurability = false;

    private readonly Config _cfg;
    private readonly DurabilityTier _tier;
    private readonly bool _useFua;
    private readonly string _dbName;
    private string _walDir;
    private ServiceProvider _sp;
    private DatabaseEngine _dbe;
    private EntityId[] _ids;
    private Transaction _readTx;

    public TyphonAdapter(Config cfg, DurabilityTier tier = DurabilityTier.D0)
    {
        _cfg = cfg;
        _tier = tier;
        _useFua = UseFua;
        _dbName = $"cb_{cfg}_{tier}_{(_useFua ? "fua" : "fsync")}_{Environment.ProcessId}";
    }

    public string Name => (_cfg switch
    {
        Config.SvLean => "Typhon SV-TickFence (I0-lean)",
        Config.SvCommitted => "Typhon SV-Committed (I0-txn)",
        Config.Versioned => "Typhon Versioned (I1/MVCC)",
        _ => "Typhon"
    }) + (_tier == DurabilityTier.D2 ? (_useFua ? " D2-FUA" : " D2-fsync") : "");

    public bool IsFloor => false;

    // D2 → FUA per commit (Immediate) unless ForceDeferredDurability; D0 → no flush (Deferred).
    private DurabilityMode WriteMode => (_tier == DurabilityTier.D2 && !ForceDeferredDurability) ? DurabilityMode.Immediate : DurabilityMode.Deferred;

    public void Load(int count)
    {

        var cache = (ulong)(32768 * PagedMMF.PageSize); // 256 MB — holds the cache-resident C0 set hot

        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry()
          .AddMemoryAllocator()
          .AddEpochManager()
          .AddHighResolutionSharedTimer()
          .AddDeadlineWatchdog()
          .AddScopedManagedPagedMemoryMappedFile(o =>
          {
              o.DatabaseName = _dbName;
              o.DatabaseCacheSize = cache;
              o.PagesDebugPattern = false;
          });

        if (_tier == DurabilityTier.D0)
        {
            // Zero-disk D0: in-mem WAL backend, no FUA, checkpoint dormant.
            sc.AddSingleton<IWalFileIO>(_ => new InMemoryWalFileIO())
              .AddScopedDatabaseEngine(o =>
              {
                  o.Wal = new WalWriterOptions { UseFUA = false };
                  o.Resources.CheckpointIntervalMs = int.MaxValue;
              });
        }
        else
        {
            // D2: production on-disk WAL with FUA (per-write durability). No IWalFileIO injected → engine builds WalFileIO.
            _walDir = Path.Combine(Path.GetTempPath(), "typhon-cb-wal", _dbName);
            Directory.CreateDirectory(_walDir);
            sc.AddScopedDatabaseEngine(o =>
            {
                o.Wal = new WalWriterOptions
                {
                    WalDirectory = _walDir,
                    // FUA=false → fsync-to-cache semantics matching SQLite/RocksDB/LMDB on write-back NVMe (a like-for-like
                    // D2). FUA=true is Typhon's *stronger* power-safe tier the others don't offer (reported separately).
                    UseFUA = _useFua,
                    SegmentSize = 16 * 1024 * 1024,
                    PreAllocateSegments = 2
                };
                o.Resources.CheckpointIntervalMs = int.MaxValue;
            });
        }

        _sp = sc.BuildServiceProvider();
        _sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _dbe = _sp.GetRequiredService<DatabaseEngine>();

        _dbe.RegisterComponentFromAccessor<SvVal>();
        _dbe.RegisterComponentFromAccessor<VVal>();
        _dbe.InitializeArchetypes();

        // Batch the bulk load: one giant WAL record per commit would exceed the 4 MB commit-buffer cap, so commit
        // every LoadBatch spawns. (Per-op writes during measurement are tiny single-record commits — no issue there.)
        const int LoadBatch = 8192;
        _ids = new EntityId[count];
        var t = _dbe.CreateQuickTransaction();
        for (int i = 0; i < count; i++)
        {
            if (_cfg == Config.Versioned)
            {
                var c = new VVal { Value = i };
                _ids[i] = t.Spawn<VValArch>(VValArch.Data.Set(in c));
            }
            else
            {
                var c = new SvVal { Value = i };
                _ids[i] = t.Spawn<SvValArch>(SvValArch.Data.Set(in c));
            }

            if ((i + 1) % LoadBatch == 0)
            {
                t.Commit();
                t.Dispose();
                t = _dbe.CreateQuickTransaction();
            }
        }
        t.Commit();
        t.Dispose();
        _dbe.WriteTickFence(1);
    }

    public IDisposable OpenReadScope()
    {
        _readTx = _dbe.CreateQuickTransaction();
        return new Scope(this);
    }

    public long PointRead(int key) => _cfg == Config.Versioned
        ? _readTx.Open(_ids[key]).Read(VValArch.Data).Value
        : _readTx.Open(_ids[key]).Read(SvValArch.Data).Value;

    public void PointWriteCommit(int key, long value)
    {
        switch (_cfg)
        {
            case Config.Versioned:
            {
                using var t = _dbe.CreateQuickTransaction(WriteMode);
                t.OpenMut(_ids[key]).Write(VValArch.Data).Value = value;
                t.Commit();
                break;
            }
            case Config.SvCommitted:
            {
                // Discipline MUST be selected at tx creation, before any write (CM-02).
                using var t = _dbe.CreateQuickTransaction(WriteMode, DurabilityDiscipline.Commit);
                t.OpenMut(_ids[key]).Write(SvValArch.Data).Value = value;
                t.Commit();
                break;
            }
            default: // SvLean / TickFence
            {
                using var t = _dbe.CreateQuickTransaction(WriteMode);
                t.OpenMut(_ids[key]).Write(SvValArch.Data).Value = value;
                t.Commit();
                break;
            }
        }
    }

    public long OnDiskBytes() => DiskUtil.Sum($"{_dbName}.bin", _walDir); // data file + WAL segments

    public void Dispose()
    {
        _readTx?.Dispose(); // normally null after the read scope; defensive (TYPHON006)
        _readTx = null;
        _dbe?.Dispose();
        _sp?.Dispose();
        try { File.Delete($"{_dbName}.bin"); } catch { }
        if (_walDir != null) { try { Directory.Delete(_walDir, true); } catch { } }
    }

    private sealed class Scope : IDisposable
    {
        private readonly TyphonAdapter _owner;
        public Scope(TyphonAdapter owner) => _owner = owner;

        public void Dispose()
        {
            _owner._readTx?.Dispose();
            _owner._readTx = null;
        }
    }
}

using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.CompetitiveBenchmark.Adapters;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.CompetitiveBenchmark.Concurrent;

/// <summary>
/// Typhon for the A6 ordered range scan (YCSB-E). Typhon's EntityMap is a hash (no key order), so the fair mapping is a
/// B+Tree SECONDARY INDEX on a long Key (Versioned storage routes index entries to the shared ComponentTable tree that
/// <see cref="Transaction.EnumerateIndex{T,TKey}"/> streams). Each worker holds a read transaction (snapshot) and drives the
/// streaming index enumerator — no query-planner/expression overhead, zero-copy component reads. Read/update/RMW are N/A here.
/// </summary>
public sealed class TyphonScanConcurrentAdapter : IConcurrentAdapter
{
    private readonly string _dbName = $"cbm_scan_{Environment.ProcessId}";
    private ServiceProvider _sp;
    private DatabaseEngine _dbe;
    private IndexRef _keyIndex;

    public string Name => "Typhon";

    public void Load(int totalCount)
    {
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry().AddMemoryAllocator().AddEpochManager()
          .AddHighResolutionSharedTimer().AddDeadlineWatchdog()
          .AddScopedManagedPagedMemoryMappedFile(o =>
          {
              o.DatabaseName = _dbName;
              o.DatabaseCacheSize = (ulong)(65536 * PagedMMF.PageSize); // 512 MB
              o.PagesDebugPattern = false;
          })
          .AddSingleton<IWalFileIO>(_ => new InMemoryWalFileIO())
          .AddScopedDatabaseEngine(o => { o.Wal = new WalWriterOptions { UseFUA = false }; o.Resources.CheckpointIntervalMs = int.MaxValue; });
        _sp = sc.BuildServiceProvider();
        _sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _dbe = _sp.GetRequiredService<DatabaseEngine>();
        _dbe.RegisterComponentFromAccessor<YcsbRec>();
        _dbe.InitializeArchetypes();

        const int LoadBatch = 8192;
        var t = _dbe.CreateQuickTransaction();
        for (int i = 0; i < totalCount; i++)
        {
            var c = new YcsbRec { Key = i, Value = i };
            t.Spawn<YcsbArch>(YcsbArch.Data.Set(in c));
            if ((i + 1) % LoadBatch == 0) { t.Commit(); t.Dispose(); t = _dbe.CreateQuickTransaction(); }
        }
        t.Commit();
        t.Dispose();
        _dbe.WriteTickFence(1);

        _keyIndex = _dbe.GetIndexRef<YcsbRec, long>(r => r.Key); // resolve once (cold), reuse across all scans
    }

    public IWorker CreateWorker() => new Worker(_dbe, _keyIndex);

    public void Dispose()
    {
        _dbe?.Dispose();
        _sp?.Dispose();
        try { File.Delete($"{_dbName}.bin"); } catch { }
    }

    private sealed class Worker : IWorker
    {
        private readonly Transaction _tx;
        private readonly IndexRef _keyIndex;

        public Worker(DatabaseEngine dbe, IndexRef keyIndex)
        {
            _tx = dbe.CreateQuickTransaction(); // read snapshot, reused across scans (this worker thread only)
            _keyIndex = keyIndex;
        }

        public long RangeScan(int startKey, int length)
        {
            long sum = 0;
            int taken = 0;
            using var e = _tx.EnumerateIndex<YcsbRec, long>(_keyIndex, startKey, long.MaxValue);
            foreach (var item in e)
            {
                sum += item.Component.Value;
                if (++taken >= length) break;
            }
            return sum;
        }

        public long ReadBatch(int startKey, int count) => throw new NotSupportedException();
        public void UpdateBatch(int startKey, int count, long seed) => throw new NotSupportedException();
        public void RmwBatch(int startKey, int count) => throw new NotSupportedException();

        public void Dispose() => _tx.Dispose();
    }
}

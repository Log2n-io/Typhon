using System;
using System.IO;
using FASTER.core;

namespace Typhon.CompetitiveBenchmark.Concurrent;

/// <summary>
/// FASTER (Microsoft Research epoch-based KV) — the closest competitor in spirit to Typhon: lock-free-ish, epoch-protected,
/// per-thread sessions. One <see cref="FasterKV{K, V}"/> shared; each worker owns a session (sessions are thread-affine).
/// The in-memory log is sized to hold the whole dataset (no disk spill), matching the other engines' fully-cached state.
/// D0: log writes are not fsync'd per op.
/// </summary>
public sealed class FasterConcurrentAdapter : IConcurrentAdapter
{
    private readonly string _dir;
    private IDevice _log;
    private FasterKV<long, long> _store;

    public FasterConcurrentAdapter(string root) => _dir = Path.Combine(root, "faster-m");

    public string Name => "FASTER";

    public void Load(int totalCount)
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
        Directory.CreateDirectory(_dir);

        _log = Devices.CreateLogDevice(Path.Combine(_dir, "hlog.log"), deleteOnClose: true);
        // index: 2^19 hash buckets for 1M keys; in-memory log 2^28 (256 MB) holds the whole dataset resident.
        _store = new FasterKV<long, long>(1L << 19, new LogSettings { LogDevice = _log, MemorySizeBits = 28, PageSizeBits = 22 });

        using var s = _store.NewSession(new SimpleFunctions<long, long>());
        for (int i = 0; i < totalCount; i++)
        {
            long k = i, v = i;
            s.Upsert(ref k, ref v);
            if ((i & 0xFFFF) == 0) s.Refresh();
        }
        s.CompletePending(true);
    }

    public IWorker CreateWorker() => new Worker(_store);

    public void Dispose()
    {
        _store?.Dispose();
        _log?.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class Worker : IWorker
    {
        private readonly ClientSession<long, long, long, long, Empty, IFunctions<long, long, long, long, Empty>> _s;
        private int _opsSinceRefresh;

        // Merger defines RMW: InitialUpdater sets value=input; In-place/Copy updaters set value=merger(input,value). With
        // input=1 and merger (input,value)=>input+value, RMW is increment-by-1. Upsert/Read ignore the merger.
        public Worker(FasterKV<long, long> store) => _s = store.NewSession(new SimpleFunctions<long, long>((input, value) => input + value));

        public long ReadBatch(int startKey, int count)
        {
            long sum = 0;
            for (int i = 0; i < count; i++)
            {
                long k = startKey + i, o = 0;
                var status = _s.Read(ref k, ref o);
                if (status.IsPending) { _s.CompletePending(true); }
                else { sum += o; }
            }
            Refresh(count);
            return sum;
        }

        public void UpdateBatch(int startKey, int count, long seed)
        {
            for (int i = 0; i < count; i++)
            {
                long k = startKey + i, v = seed + i;
                _s.Upsert(ref k, ref v);
            }
            Refresh(count);
        }

        // Native FASTER RMW — increment via the session's adder merger. The hottest, most idiomatic RMW in the field.
        public void RmwBatch(int startKey, int count)
        {
            for (int i = 0; i < count; i++)
            {
                long k = startKey + i, input = 1, output = 0;
                var status = _s.RMW(ref k, ref input, ref output);
                if (status.IsPending) _s.CompletePending(true);
            }
            Refresh(count);
        }

        // FASTER is a hash index — no key-ordered cursor exists. YCSB-E range scan is N/A.
        public long RangeScan(int startKey, int length) =>
            throw new NotSupportedException("FASTER has no ordered range scan (hash index).");

        // FASTER sessions must periodically Refresh to let the epoch advance (memory reclamation); batch it to stay cheap.
        private void Refresh(int count)
        {
            _opsSinceRefresh += count;
            if (_opsSinceRefresh >= 256) { _s.Refresh(); _opsSinceRefresh = 0; }
        }

        public void Dispose() => _s.Dispose();
    }
}

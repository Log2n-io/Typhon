using System;
using System.IO;
using FASTER.core;

namespace Typhon.CompetitiveBenchmark.Adapters;

/// <summary>
/// Floor reference: FASTER (Microsoft.FASTER.Core) — bare in-memory log-structured KV, no isolation, no durable commit.
/// Establishes the hardware ceiling for "minimal work per op" on this box. NOT an opponent (see plan §0 / §9).
/// </summary>
public sealed class FasterAdapter : IEngineAdapter
{
    private readonly string _dir;
    private IDevice _log;
    private FasterKV<long, long> _store;
    private ClientSession<long, long, long, long, Empty, IFunctions<long, long, long, long, Empty>> _session;

    // FASTER is the in-memory floor reference. fsync-per-commit (D2) is not its native model (checkpoint-based), so the
    // tier is accepted for a uniform construction signature but the engine always runs in-memory; D2 results are N/A.
    public FasterAdapter(string rootDir, DurabilityTier tier = DurabilityTier.D0)
    {
        _dir = Path.Combine(rootDir, "faster");
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
        Directory.CreateDirectory(_dir);
    }

    public string Name => "FASTER (floor)";
    public bool IsFloor => true;

    public void Load(int count)
    {
        _log = Devices.CreateLogDevice(Path.Combine(_dir, "hlog.log"), deleteOnClose: true);
        // Mutable region sized to hold the whole working set in memory (C0 is cache-resident): 2^26 = 64 MB.
        _store = new FasterKV<long, long>(
            size: 1L << 20,
            logSettings: new LogSettings { LogDevice = _log, MemorySizeBits = 26, PageSizeBits = 20 });
        _session = _store.NewSession(new SimpleFunctions<long, long>());

        for (long k = 0; k < count; k++)
        {
            long v = k;
            _session.Upsert(ref k, ref v);
        }
    }

    public IDisposable OpenReadScope() => NoScope.Instance;

    public long PointRead(int key)
    {
        long k = key, o = 0;
        var status = _session.Read(ref k, ref o);
        if (status.IsPending)
        {
            _session.CompletePending(wait: true); // never expected in-memory; defensive
        }
        return o;
    }

    public void PointWriteCommit(int key, long value)
    {
        long k = key, v = value;
        _session.Upsert(ref k, ref v); // floor = in-memory upsert, no durable commit
    }

    public long OnDiskBytes() => DiskUtil.Sum(_dir); // hlog segment files

    public void Dispose()
    {
        _session?.Dispose();
        _store?.Dispose();
        _log?.Dispose();
    }
}

/// <summary>No-op read scope for adapters that don't need a shared read transaction (FASTER).</summary>
public sealed class NoScope : IDisposable
{
    public static readonly NoScope Instance = new();
    public void Dispose() { }
}

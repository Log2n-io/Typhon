using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Typhon.Engine.Internals;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// The fence's hot-path sorts, each in the shape its site sorts — element size, key width, key distribution — under the algorithm the site runs today and
// under the generic RadixSort. Measured BEFORE any site was converted, so the decision to convert one rests on this and not on the queue sort's number.
//
// Every arm copies a pristine array into the working one first, so both arms pay the same copy and the Copy* rows say how much of each number is that.
//
// Run: dotnet run --project test/Typhon.Benchmark -c Release -- --filter '*RadixSortBenchmarks*'
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

[SimpleJob(warmupCount: 3, iterationCount: 7)]
[MemoryDiagnoser]
[BenchmarkCategory("RadixSort")]
public class RadixSortBenchmarks
{
    /// <summary>A repair unit or a Migrate chunk's run at W=8 (128, 512), the queue at a quiet tick (2 048), a whole batch on the serial fence
    /// (32 768).</summary>
    [Params(128, 512, 2_048, 32_768)]
    public int N;

    private readonly int[] _counts = new int[RadixSort.Buckets];

    // Migrate's pending queue: 20-byte requests, 32-bit cell key over a few thousand live cells.
    private MigrationRequest[] _reqPristine, _reqWork, _reqScratch;

    // EntityMap staging: 48-byte patches, bucket index below LiveBucketCount.
    private EntityLocationUpdate[] _mapPristine, _mapWork, _mapScratch;
    private int[] _mapKeys;

    // Index staging: AllowMultiple entries — 16 bytes with an int key, 24 with a long key.
    private BTreeMultiValueUpdate<int>[] _idxIntPristine, _idxIntWork, _idxIntScratch;
    private BTreeMultiValueUpdate<long>[] _idxLongPristine, _idxLongWork, _idxLongScratch;

    // The repair planner's Morton sort: 16-byte entries, a 62-bit random Morton key (the site's is 63) with a (chunkId * 64 + slot) tie-break over the
    // sparse chunk ids a real archetype has (up to 2^25), so the tie-break pass count matches the site's rather than a dense 0..N range.
    private ArchetypeClusterState.RepairEntry[] _repPristine, _repWork, _repScratch;

    // Migrate's dirty-bit deltas: 32-byte structs grouped by a ushort archetype id — one archetype (the common tick), and four.
    private DirtyBitDelta[] _dirtyOnePristine, _dirtyFourPristine, _dirtyScratch;
    private List<DirtyBitDelta> _dirtyList;

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(889);
        var cells = Math.Max(16, N / 5);

        _reqPristine = new MigrationRequest[N];
        for (var i = 0; i < N; i++)
        {
            _reqPristine[i] = new MigrationRequest(sourceClusterChunkId: i, sourceSlotIndex: i & 63, destCellKey: rng.Next(cells),
                destClusterChunkId: rng.Next(1 << 16));
        }

        _reqWork = new MigrationRequest[N];
        _reqScratch = new MigrationRequest[N];

        _mapPristine = new EntityLocationUpdate[N];
        for (var i = 0; i < N; i++)
        {
            _mapPristine[i] = new EntityLocationUpdate
            {
                EntityKey = rng.NextInt64(), Bucket = rng.Next(1 << 16), DstChunkId = rng.Next(1 << 20), DstSlot = rng.Next(64),
            };
        }

        _mapWork = new EntityLocationUpdate[N];
        _mapScratch = new EntityLocationUpdate[N];
        _mapKeys = new int[N];

        _idxIntPristine = new BTreeMultiValueUpdate<int>[N];
        _idxLongPristine = new BTreeMultiValueUpdate<long>[N];
        for (var i = 0; i < N; i++)
        {
            _idxIntPristine[i] = new BTreeMultiValueUpdate<int>(rng.Next(1 << 20), rng.Next(1 << 16), rng.Next(), rng.Next());
            _idxLongPristine[i] = new BTreeMultiValueUpdate<long>(rng.NextInt64(1L << 40), rng.Next(1 << 16), rng.Next(), rng.Next());
        }

        _idxIntWork = new BTreeMultiValueUpdate<int>[N];
        _idxIntScratch = new BTreeMultiValueUpdate<int>[N];
        _idxLongWork = new BTreeMultiValueUpdate<long>[N];
        _idxLongScratch = new BTreeMultiValueUpdate<long>[N];

        _repPristine = new ArchetypeClusterState.RepairEntry[N];
        for (var i = 0; i < N; i++)
        {
            _repPristine[i] = new ArchetypeClusterState.RepairEntry((ulong)rng.NextInt64() >> 1, (long)rng.Next(1 << 25) * 64 + rng.Next(64));
        }

        _repWork = new ArchetypeClusterState.RepairEntry[N];
        _repScratch = new ArchetypeClusterState.RepairEntry[N];

        _dirtyOnePristine = new DirtyBitDelta[N];
        _dirtyFourPristine = new DirtyBitDelta[N];
        for (var i = 0; i < N; i++)
        {
            _dirtyOnePristine[i] = new DirtyBitDelta { ArchetypeId = 3, SrcChunkId = i, SrcClearMask = 1L << (i & 63), DstChunkId = i + 1, DstSetMask = 1 };
            _dirtyFourPristine[i] = _dirtyOnePristine[i];
            _dirtyFourPristine[i].ArchetypeId = (ushort)(rng.Next(4) * 7 + 1);
        }

        _dirtyScratch = new DirtyBitDelta[N];
        _dirtyList = new List<DirtyBitDelta>(N);
    }

    // ── Copy baselines ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Benchmark]
    public void Copy_16B() => Array.Copy(_idxIntPristine, _idxIntWork, N);

    [Benchmark]
    public void Copy_20B() => Array.Copy(_reqPristine, _reqWork, N);

    [Benchmark]
    public void Copy_24B() => Array.Copy(_idxLongPristine, _idxLongWork, N);

    [Benchmark]
    public void Copy_32B()
    {
        _dirtyList.Clear();
        _dirtyList.AddRange(_dirtyOnePristine);
    }

    [Benchmark]
    public void Copy_48B() => Array.Copy(_mapPristine, _mapWork, N);

    // ── Migrate queue by destination cell ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Benchmark]
    public void Queue_ArraySort_IComparer()
    {
        Array.Copy(_reqPristine, _reqWork, N);
        Array.Sort(_reqWork, 0, N, ReqComparer.Instance);
    }

    [Benchmark]
    public void Queue_Radix()
    {
        Array.Copy(_reqPristine, _reqWork, N);
        RadixSort.Sort<MigrationRequest, ReqKey>(_reqWork, _reqScratch, _counts);
    }

    // ── EntityMap staging by bucket ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Benchmark]
    public void Map_ArraySort_KeysItems()
    {
        Array.Copy(_mapPristine, _mapWork, N);
        for (var i = 0; i < N; i++)
        {
            _mapKeys[i] = _mapWork[i].Bucket;
        }

        Array.Sort(_mapKeys, _mapWork, 0, N);
    }

    [Benchmark]
    public void Map_Radix()
    {
        Array.Copy(_mapPristine, _mapWork, N);
        RadixSort.Sort<EntityLocationUpdate, MapKey>(_mapWork, _mapScratch, _counts);
    }

    // ── Index staging by key ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Benchmark]
    public void IndexInt_SpanSort_StructComparer()
    {
        Array.Copy(_idxIntPristine, _idxIntWork, N);
        _idxIntWork.AsSpan(0, N).Sort(default(IdxIntComparer));
    }

    [Benchmark]
    public void IndexInt_Radix()
    {
        Array.Copy(_idxIntPristine, _idxIntWork, N);
        RadixSort.Sort<BTreeMultiValueUpdate<int>, IdxIntKey>(_idxIntWork, _idxIntScratch, _counts);
    }

    [Benchmark]
    public void IndexLong_SpanSort_StructComparer()
    {
        Array.Copy(_idxLongPristine, _idxLongWork, N);
        _idxLongWork.AsSpan(0, N).Sort(default(IdxLongComparer));
    }

    [Benchmark]
    public void IndexLong_Radix()
    {
        Array.Copy(_idxLongPristine, _idxLongWork, N);
        RadixSort.Sort<BTreeMultiValueUpdate<long>, IdxLongKey>(_idxLongWork, _idxLongScratch, _counts);
    }

    // ── Repair planner by (Morton, source) ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Benchmark]
    public void Repair_ArraySort_IComparable()
    {
        Array.Copy(_repPristine, _repWork, N);
        Array.Sort(_repWork, 0, N);
    }

    /// <summary>Minor key first, major key second, both stable: the lexicographic (Morton, source) order the planner's comparator defines.</summary>
    [Benchmark]
    public void Repair_Radix_TwoKeys()
    {
        Array.Copy(_repPristine, _repWork, N);
        RadixSort.Sort<ArchetypeClusterState.RepairEntry, RepSourceKey>(_repWork, _repScratch, _counts);
        RadixSort.Sort<ArchetypeClusterState.RepairEntry, RepMortonKey>(_repWork, _repScratch, _counts);
    }

    // ── Dirty-bit deltas by archetype ────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Benchmark]
    public void DirtyOneArchetype_ListSort_Delegate()
    {
        _dirtyList.Clear();
        _dirtyList.AddRange(_dirtyOnePristine);
        _dirtyList.Sort(static (a, b) => a.ArchetypeId.CompareTo(b.ArchetypeId));
    }

    [Benchmark]
    public void DirtyOneArchetype_Radix()
    {
        _dirtyList.Clear();
        _dirtyList.AddRange(_dirtyOnePristine);
        RadixSort.Sort<DirtyBitDelta, DirtyKey>(CollectionsMarshal.AsSpan(_dirtyList), _dirtyScratch, _counts);
    }

    [Benchmark]
    public void DirtyFourArchetypes_ListSort_Delegate()
    {
        _dirtyList.Clear();
        _dirtyList.AddRange(_dirtyFourPristine);
        _dirtyList.Sort(static (a, b) => a.ArchetypeId.CompareTo(b.ArchetypeId));
    }

    [Benchmark]
    public void DirtyFourArchetypes_Radix()
    {
        _dirtyList.Clear();
        _dirtyList.AddRange(_dirtyFourPristine);
        RadixSort.Sort<DirtyBitDelta, DirtyKey>(CollectionsMarshal.AsSpan(_dirtyList), _dirtyScratch, _counts);
    }

    // ── Keys and comparers, as the sites define them ─────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class ReqComparer : IComparer<MigrationRequest>
    {
        public static readonly ReqComparer Instance = new();

        public int Compare(MigrationRequest x, MigrationRequest y) => x.DestCellKey.CompareTo(y.DestCellKey);
    }

    private readonly struct ReqKey : IRadixKey<MigrationRequest>
    {
        public static ulong Key(in MigrationRequest item) => RadixSort.SignedKey(item.DestCellKey);
    }

    private readonly struct MapKey : IRadixKey<EntityLocationUpdate>
    {
        public static ulong Key(in EntityLocationUpdate item) => RadixSort.SignedKey(item.Bucket);
    }

    private readonly struct IdxIntComparer : IComparer<BTreeMultiValueUpdate<int>>
    {
        public int Compare(BTreeMultiValueUpdate<int> x, BTreeMultiValueUpdate<int> y) => x.Key.CompareTo(y.Key);
    }

    private readonly struct IdxIntKey : IRadixKey<BTreeMultiValueUpdate<int>>
    {
        public static ulong Key(in BTreeMultiValueUpdate<int> item) => RadixSort.SignedKey(item.Key);
    }

    private readonly struct IdxLongComparer : IComparer<BTreeMultiValueUpdate<long>>
    {
        public int Compare(BTreeMultiValueUpdate<long> x, BTreeMultiValueUpdate<long> y) => x.Key.CompareTo(y.Key);
    }

    private readonly struct IdxLongKey : IRadixKey<BTreeMultiValueUpdate<long>>
    {
        public static ulong Key(in BTreeMultiValueUpdate<long> item) => RadixSort.SignedKey(item.Key);
    }

    private readonly struct RepMortonKey : IRadixKey<ArchetypeClusterState.RepairEntry>
    {
        public static ulong Key(in ArchetypeClusterState.RepairEntry item) => item.MortonKey;
    }

    private readonly struct RepSourceKey : IRadixKey<ArchetypeClusterState.RepairEntry>
    {
        public static ulong Key(in ArchetypeClusterState.RepairEntry item) => RadixSort.SignedKey(item.SourceLocation);
    }

    private readonly struct DirtyKey : IRadixKey<DirtyBitDelta>
    {
        public static ulong Key(in DirtyBitDelta item) => item.ArchetypeId;
    }
}

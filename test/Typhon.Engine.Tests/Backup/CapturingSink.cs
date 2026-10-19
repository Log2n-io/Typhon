using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Typhon.Engine.Tests;

/// <summary>
/// A test-side backup store (#1413 owns the real one): keeps every point it is given in memory and enforces the seam's contract
/// (<c>BackupPointSeam.cs</c>), so a capture that breaks the call order fails here rather than in a pack writer.
/// </summary>
internal sealed class FakeBackupStore : IBackupPointStore
{
    private uint _nextBackupId = 1;

    /// <summary>The store's identity.</summary>
    public Guid StoreId { get; set; } = Guid.NewGuid();

    /// <summary>Every completed point, in completion order.</summary>
    public List<CapturingSink> Points { get; } = [];

    /// <summary>Every point begun, completed or not.</summary>
    public List<CapturingSink> Begun { get; } = [];

    /// <summary>The database the store belongs to, bound by the first session (<see cref="Guid.Empty"/> until then).</summary>
    public Guid DatabaseId { get; private set; }

    /// <summary>Called on every new sink, before the capture uses it: a test arms its hooks here.</summary>
    public Action<CapturingSink> OnBeginPoint { get; set; }

    /// <summary>Forget every point (a wiped or replaced store).</summary>
    public void Wipe()
    {
        Points.Clear();
        StoreId = Guid.NewGuid();
    }

    public IBackupStoreSession OpenSession(Guid databaseId, CancellationToken ct)
    {
        if (DatabaseId != Guid.Empty && DatabaseId != databaseId)
        {
            throw new InvalidOperationException("The store belongs to another database.");
        }

        DatabaseId = databaseId;
        return new Session(this);
    }

    private sealed class Session : IBackupStoreSession
    {
        private readonly FakeBackupStore _store;

        public Session(FakeBackupStore store) => _store = store;

        public Guid StoreId => _store.StoreId;

        public bool TryGetChainHead(Guid chainId, out BackupChainHead head)
        {
            for (var i = _store.Points.Count - 1; i >= 0; i--)
            {
                var p = _store.Points[i];
                if (p.ChainId == chainId)
                {
                    head = new BackupChainHead(p.BackupId, p.Plan.Kind, p.BaseId, p.PointLsn, p.Plan.TimelineId);
                    return true;
                }
            }

            head = default;
            return false;
        }

        public IBackupPointSink BeginPoint(in BackupPointPlan plan)
        {
            if (plan.Kind == BackupKind.Incremental)
            {
                if (!TryGetChainHead(plan.ChainId, out var head) || head.BackupId != plan.ParentId)
                {
                    throw new InvalidOperationException("The incremental's parent is not its chain's head.");
                }
            }
            else if (plan.ChainId != Guid.Empty)
            {
                throw new InvalidOperationException("A full point's chain is minted by the store.");
            }

            var sink = new CapturingSink(_store, plan, _store._nextBackupId++);
            _store.Begun.Add(sink);
            _store.OnBeginPoint?.Invoke(sink);
            return sink;
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>One point being captured into a <see cref="FakeBackupStore"/>.</summary>
internal sealed class CapturingSink : IBackupPointSink
{
    private readonly FakeBackupStore _store;
    private TailSegment _openSegment;
    private bool _imageSet;

    public CapturingSink(FakeBackupStore store, BackupPointPlan plan, uint backupId)
    {
        _store = store;
        Plan = plan;
        BackupId = backupId;
        ChainId = plan.Kind == BackupKind.Full ? Guid.NewGuid() : plan.ChainId;
        BaseId = plan.Kind == BackupKind.Full ? backupId : ParentBaseId(store, plan);
    }

    public BackupPointPlan Plan { get; }

    public uint BackupId { get; }

    public Guid ChainId { get; }

    public uint BaseId { get; }

    public int ImagePages { get; private set; }

    public long CheckpointLsn { get; private set; }

    public long FreezeUtcTicks { get; private set; }

    public long PointLsn { get; private set; }

    public bool Completed { get; private set; }

    public bool Aborted { get; private set; }

    /// <summary>Page images by file page.</summary>
    public Dictionary<int, byte[]> Pages { get; } = [];

    /// <summary>Pages reported suspect.</summary>
    public HashSet<int> Suspects { get; } = [];

    /// <summary>The tail, in the order given.</summary>
    public List<TailSegment> Tail { get; } = [];

    /// <summary>Invoked at the start of each <see cref="AddPage"/>, on the capture thread: a test may block or throw here.</summary>
    public Action<int> OnAddPage { get; set; }

    /// <summary>Invoked at the start of <see cref="Complete"/>: a test may block or throw here.</summary>
    public Action OnComplete { get; set; }

    public void SetImage(int imagePages, long checkpointLsn, long freezeUtcTicks)
    {
        Require(!_imageSet && Pages.Count == 0 && Tail.Count == 0, "SetImage is called once, before any page or tail");
        _imageSet = true;
        ImagePages = imagePages;
        CheckpointLsn = checkpointLsn;
        FreezeUtcTicks = freezeUtcTicks;
    }

    public void AddPage(int pageId, ReadOnlySpan<byte> image, bool suspect)
    {
        OnAddPage?.Invoke(pageId);
        Require(_imageSet, "AddPage after SetImage");
        Require(pageId >= 0 && pageId < ImagePages, $"page {pageId} inside [0, {ImagePages})");
        Require(image.Length == PagedMMF.PageSize, "a page image is 8 KiB");
        Require(Pages.TryAdd(pageId, image.ToArray()), $"page {pageId} added once");
        if (suspect)
        {
            Suspects.Add(pageId);
        }
    }

    public void BeginTailSegment(long segmentId, long firstLsn, uint declaredSize)
    {
        Require(_imageSet && _openSegment == null, "tail segments follow SetImage and never nest");
        Require(Tail.Count == 0 || Tail[^1].SegmentId < segmentId, "tail segments ascend");
        _openSegment = new TailSegment(segmentId, firstLsn, declaredSize);
    }

    public void AppendTail(ReadOnlySpan<byte> bytes)
    {
        Require(_openSegment != null, "AppendTail inside a segment");
        _openSegment.Bytes.Write(bytes);
    }

    public void EndTailSegment()
    {
        Require(_openSegment != null, "EndTailSegment closes an open segment");
        Require(_openSegment.Bytes.Length <= _openSegment.DeclaredSize, "a segment's prefix fits its declared size");
        Tail.Add(_openSegment);
        _openSegment = null;
    }

    public BackupPointInfo Complete(long pointLsn, long tailEndUtcTicks)
    {
        OnComplete?.Invoke();
        Require(_imageSet && _openSegment == null && !Aborted, "Complete after the image and the tail");
        Require(Pages.ContainsKey(0) && Pages.ContainsKey(1), "pages 0 and 1 always come");
        if (Plan.Kind == BackupKind.Full)
        {
            Require(Pages.Count == ImagePages, "a full point holds every page");
        }

        PointLsn = pointLsn;
        Completed = true;
        _store.Points.Add(this);
        return new BackupPointInfo(BackupId, ChainId, Plan.Kind, pointLsn, CheckpointLsn, ImagePages, Pages.Count, Suspects.Count, 0, "memory");
    }

    public void Abort() => Aborted = true;

    private static uint ParentBaseId(FakeBackupStore store, BackupPointPlan plan)
    {
        foreach (var p in store.Points)
        {
            if (p.BackupId == plan.ParentId)
            {
                return p.BaseId;
            }
        }

        return 0;
    }

    private static void Require(bool condition, string contract)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Seam contract broken: {contract}.");
        }
    }

    /// <summary>One WAL segment of a captured tail.</summary>
    internal sealed class TailSegment
    {
        public TailSegment(long segmentId, long firstLsn, uint declaredSize)
        {
            SegmentId = segmentId;
            FirstLsn = firstLsn;
            DeclaredSize = declaredSize;
        }

        public long SegmentId { get; }

        public long FirstLsn { get; }

        public uint DeclaredSize { get; }

        public MemoryStream Bytes { get; } = new();
    }
}

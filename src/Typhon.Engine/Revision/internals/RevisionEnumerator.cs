using JetBrains.Annotations;
using System;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

[PublicAPI]
internal ref struct RevisionEnumerator : IDisposable
{
    private ref ChunkAccessor<PersistentStore> _compRevTableAccessor;
    private ref CompRevStorageHeader _header;
    private Span<CompRevStorageElement> _elements;
    private readonly int _firstChunkId;
    private short _itemCountLeft;
    private short _indexInChunk;
    private ref int _nextChunkId;
    private readonly bool _exclusiveAccess;
    private readonly bool _ownsLock;
    private readonly PagedMMF.CaptureTicket _ticket;
    private bool _hasLopped;
    private short _revisionIndex;

    public ref CompRevStorageHeader Header => ref _header;
    public int RevisionIndex => _revisionIndex;
    public int IndexInChunk => _indexInChunk;
    public bool HasLopped => _hasLopped;

    public ref CompRevStorageElement Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        get
        {
            if (_itemCountLeft >= 0)
            {
                return ref _elements[_indexInChunk];
            }
            return ref Unsafe.NullRef<CompRevStorageElement>();
        }
    }

    public ref int NextChunkId => ref _nextChunkId;
    public Span<CompRevStorageElement> Elements => _elements;
    public Span<CompRevStorageElement> CurrentAsSpan => _elements.Slice(_indexInChunk, 1);
    public int CurChunkId { get; private set; }
    public bool IsFirstChunk => CurChunkId == _firstChunkId;

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool MoveNext()
    {
        if (--_itemCountLeft < 0)
        {
            return false;
        }

        ++_revisionIndex;
        if (++_indexInChunk == _elements.Length)
        {
            _indexInChunk = 0;
            if (!StepToChunk(1, true))
            {
                return false;
            }
        }

        return true;
    }

    /// <remarks>
    /// <c>budgetOwner</c> is the transactional read path (CX-06): when set, <c>lockWc</c> is ignored. The lock is first tried without waiting
    /// (<see cref="AccessControlSmall.TryEnterSharedAccess"/> / <see cref="AccessControlSmall.TryEnterExclusiveAccess"/>), which reads no clock and
    /// reports no contention; only an acquisition that has to wait composes its budget, from the transaction, at that moment.
    /// </remarks>
    public unsafe RevisionEnumerator(ref ChunkAccessor<PersistentStore> compRevTableAccessor, int compRevFirstChunkId, bool exclusiveAccess, bool goToFirstItem,
        WaitContext lockWc, Transaction budgetOwner = null)
    {
        _compRevTableAccessor = ref compRevTableAccessor;
        _exclusiveAccess = exclusiveAccess;
        _firstChunkId = compRevFirstChunkId;
        _header = ref compRevTableAccessor.GetChunk<CompRevStorageHeader>(compRevFirstChunkId);
        _ownsLock = !_header.Control.IsLockedByCurrentThread;
        if (_ownsLock)
        {
            _ticket = ComponentRevisionManager.ChainLockTicket(ref compRevTableAccessor, ref _header);

            // The budget is the caller's — the infinite PTA context, a fresh per-call one — or, on the transactional path, composed only if the
            // uncontended attempt fails (CX-06). Either way it is only consulted under contention.
            if (budgetOwner != null)
            {
                var acquired = _exclusiveAccess ? _header.Control.TryEnterExclusiveAccess() : _header.Control.TryEnterSharedAccess();
                if (!acquired)
                {
                    lockWc = budgetOwner.ChainLockWaitContext();
                    if (!_header.Control.Enter(_exclusiveAccess, ref lockWc))
                    {
                        ThrowHelper.ThrowLockTimeout("RevisionChain/Enumerate", TimeoutOptions.Current.RevisionChainLockTimeout);
                    }
                }
            }
            else if (!_header.Control.Enter(_exclusiveAccess, ref lockWc))
            {
                ThrowHelper.ThrowLockTimeout("RevisionChain/Enumerate", TimeoutOptions.Current.RevisionChainLockTimeout);
            }
        }
        // The step to an overflow chunk fetches a page, and a fetch can throw: a back-pressure timeout, or an entity open's restart (#1230), after which
        // the transaction goes on. The constructor never returned, so the caller's using has nothing to dispose: the hold is released here, or the
        // retry takes the shared lock again over one never given back and the next writer of the entity waits out its lock timeout (as #1278).
        try
        {
            _itemCountLeft = _header.ItemCount;
            _nextChunkId = ref _header.NextChunkId;

            _indexInChunk = goToFirstItem ? _header.FirstItemIndex : (short)0;
            if (_indexInChunk < ComponentRevisionManager.CompRevCountInRoot)
            {
                var chunkContent = compRevTableAccessor.GetChunkAsSpan(compRevFirstChunkId, false);
                _nextChunkId = ref chunkContent.Cast<byte, int>()[0];
                _elements = chunkContent.Slice(sizeof(CompRevStorageHeader)).Cast<byte, CompRevStorageElement>();
                CurChunkId = compRevFirstChunkId;
            }
            else
            {
                var (chunkIndexInChain, index) = CompRevStorageHeader.GetRevisionLocation(_indexInChunk);
                _indexInChunk = (short)index;
                StepToChunk(chunkIndexInChain, false);
            }
        }
        catch
        {
            Dispose();
            throw;
        }

        --_indexInChunk;        // We pre-increment in MoveNext, so we start one before
        _revisionIndex = -1;
    }

    public unsafe bool StepToChunk(int stepCount, bool loop)
    {
        for (int i = 0; i < stepCount; i++)
        {
            if (_nextChunkId == 0)
            {
                if (loop)
                {
                    CurChunkId = _firstChunkId;
                    var chunkSpan = _compRevTableAccessor.GetChunkAsSpan(_firstChunkId, false);
                    _nextChunkId = ref chunkSpan.Cast<byte, int>()[0];
                    _elements = chunkSpan.Slice(sizeof(CompRevStorageHeader)).Cast<byte, CompRevStorageElement>();
                    _hasLopped = true;
                    return true;
                }

                CurChunkId = -1;
                _nextChunkId = ref Unsafe.NullRef<int>();
                _elements = Span<CompRevStorageElement>.Empty;
                return false;
            }

            {
                CurChunkId = _nextChunkId;
                var chunkSpan = _compRevTableAccessor.GetChunkAsSpan(_nextChunkId, false);
                _nextChunkId = ref chunkSpan.Cast<byte, int>()[0];
                _elements = chunkSpan.Slice(sizeof(int)).Cast<byte, CompRevStorageElement>().Slice(0, ComponentRevisionManager.CompRevCountInNext);
            }
        }
        return true;
    }

    public void Dispose()
    {
        if (_ownsLock)
        {
            _header.Control.Exit(_exclusiveAccess);
            ComponentRevisionManager.EndChainLockHold(ref _compRevTableAccessor, in _ticket);
        }
    }
}

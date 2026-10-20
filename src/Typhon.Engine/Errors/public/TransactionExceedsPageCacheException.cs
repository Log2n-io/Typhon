using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// A commit needs more of the page cache than the cache has: the pages it holds itself — its own dirty marks, and the pages its own epoch pin keeps —
/// fill every slot a wait could get back, so no amount of waiting can serve it. Thrown before the commit's WAL append; the transaction is rolled back.
/// Not transient: the same commit fails the same way. Commit in smaller batches, or enlarge the page cache.
/// </summary>
[PublicAPI]
public class TransactionExceedsPageCacheException : StorageException
{
    /// <summary>
    /// Creates a new <see cref="TransactionExceedsPageCacheException"/>.
    /// </summary>
    /// <param name="heldPages">Pages the commit held when it was refused: its marks and its pin's pages, each counted once.</param>
    /// <param name="cachePages">Pages in the cache.</param>
    /// <param name="markedPages">Pages under the commit's own dirty marks.</param>
    /// <param name="pinnedPages">Pages kept by the commit's own epoch pin, the cache's floor; 0 when its pin was not the floor.</param>
    public TransactionExceedsPageCacheException(int heldPages, int cachePages, int markedPages, int pinnedPages)
        : base(TyphonErrorCode.StorageCapacityExceeded,
            $"The commit needs more of the page cache than it has: it holds {heldPages} of the cache's {cachePages} pages ({markedPages} under its " +
            $"own dirty marks, {pinnedPages} kept by its own epoch pin) and no other page can be freed for it. Commit in smaller batches, or enlarge " +
            "the page cache")
    {
        HeldPages = heldPages;
        CachePages = cachePages;
        MarkedPages = markedPages;
        PinnedPages = pinnedPages;
    }

    /// <summary>Pages the commit held when it was refused: its marks and its pin's pages, each counted once.</summary>
    public int HeldPages { get; }

    /// <summary>Pages in the cache.</summary>
    public int CachePages { get; }

    /// <summary>Pages under the commit's own dirty marks.</summary>
    public int MarkedPages { get; }

    /// <summary>Pages kept by the commit's own epoch pin, the cache's floor; 0 when its pin was not the floor.</summary>
    public int PinnedPages { get; }
}

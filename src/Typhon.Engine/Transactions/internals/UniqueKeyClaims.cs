using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// A key in one unique index, by reference: equal and hashed by the index's own key comparison, so a floating-point -0 and +0 are one key, as are any two
/// NaNs.
/// </summary>
/// <remarks>
/// <b>The bytes are not copied, so the pointer's target must stay put and unchanged for as long as the reference is held.</b> Both holders satisfy that by
/// construction and neither outlives a commit: the validation's own per-thread sets live inside one
/// <c>ValidatePublishBeforeAppend</c>, and <see cref="UniqueKeyClaims"/>' entries live from there to the end of <c>Commit</c> — over staged spawn payloads
/// and content chunks that the publish reads rather than writes, inside the committer's epoch scope, released before <c>Dispose</c> can recycle any of it.
/// </remarks>
internal readonly unsafe struct UniqueKeyRef : IEquatable<UniqueKeyRef>
{
    private readonly IBTreeIndex _index;
    private readonly byte* _key;
    private readonly int _hash;

    public UniqueKeyRef(IBTreeIndex index, byte* key)
    {
        _index = index;
        _key = key;
        _hash = HashCode.Combine(RuntimeHelpers.GetHashCode(index), index.KeyHash(key));
    }

    public bool Equals(UniqueKeyRef other) => ReferenceEquals(_index, other._index) && _index.KeysEqual(_key, other._key);

    public override bool Equals(object obj) => obj is UniqueKeyRef other && Equals(other);

    public override int GetHashCode() => _hash;
}

/// <summary>
/// The unique-index keys the engine's in-flight commits have validated and not yet published — one claim per key, so two commits cannot both pass
/// validation for one key and then collide in their publishes.
/// </summary>
/// <remarks>
/// <para>
/// <b>This closes the window that made an ordinary uniqueness race stop the engine</b> (#1390). <c>ValidatePublishBeforeAppend</c> (AP-03) descends the
/// unique index and refuses a key another entity holds, deliberately before the append so the refusal is a rollback the caller can catch. But the check
/// read only the TREE, and nothing had published yet: N threads spawning one key all found it free, all appended, and the first publish to insert it won
/// while every other publish threw <c>UniqueConstraintViolationException</c> from <c>BTree.Insert</c> — past the point of no return, where AP-02 says a
/// failure can only stop the engine. The caller asked for "that name is taken" and got <c>CommitPipelineStopped</c> and a database to reopen.
/// </para>
/// <para>
/// A claim is what the tree cannot say yet: <i>a commit you cannot see intends this key</i>. Taken during validation, released at the end of
/// <c>Commit</c> — after the publish has put the key in the tree, so the next commit's own descent finds it there and needs no claim to refuse it. The
/// losers are refused where every other validation failure is refused, before the append, with the exception their caller already handles.
/// </para>
/// <para>
/// <b>What a claim deliberately does NOT do is wait.</b> A commit refused by a live claim is told the key is taken, and if the claim's owner then aborts
/// the key was in fact free — a spurious refusal, where a lock-waiting implementation would have let the loser through. That trade is taken knowingly:
/// waiting on several keys needs a global acquisition order or it deadlocks two commits against each other, and the alternative being replaced is not
/// "the loser waits" but "the engine stops". A caller that must distinguish retries. If waiting is ever added, the keys of one commit have to be ordered.
/// </para>
/// <para>
/// <b>Cost is one lock acquisition and one hash insert per unique key written</b>, and only for a commit that writes one: <c>AnySpawnNeedsCheck</c> and its
/// siblings gate the whole validation, so a commit touching no unique index never reaches this. The critical section is a hash-set operation, held under a
/// plain monitor rather than a striped structure because the commits that contend are already paying optimistic B+Tree descents on the same keys — and
/// because a claim must be visible to every other committer the instant it exists, which is what makes one lock the simple answer rather than the lazy one.
/// </para>
/// </remarks>
internal sealed unsafe class UniqueKeyClaims
{
    private readonly HashSet<UniqueKeyRef> _held = [];
    private readonly object _gate = new();

    /// <summary>How many keys are claimed right now. For tests and diagnostics; a claim that outlives its commit is a leak this number shows.</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _held.Count;
            }
        }
    }

    /// <summary>Zeroes <see cref="ClaimsTaken"/> between the arms of a measurement.</summary>
    internal void ResetCountersForTest() => System.Threading.Interlocked.Exchange(ref ClaimsTaken, 0);

    /// <summary>
    /// Admits a claim another commit already holds, so a test can run the race against a production path that does not refuse it (AP-03's mutant).
    /// </summary>
    /// <remarks>
    /// On the production path and not on a copy of it, which is the point: a mutant built out of a parallel implementation proves only that the copy is
    /// wrong. It removes exactly one thing, the refusal — and with it the invariant that one key has one claimant, so under this flag two commits believe
    /// they hold one key and the first release empties it for both. That is the state the refusal exists to prevent, so the flag is never set for a case
    /// that reads <see cref="Count"/>.
    /// </remarks>
    internal bool AdmitClaimedKeysForTest { get; set; }

    /// <summary>Takes no claim at all, so the cost of claiming can be measured against the same binary without it.</summary>
    /// <remarks>
    /// <b>For an A/B of this file's own overhead and nothing else.</b> With it set, the engine behaves as it did before #1390 — a
    /// uniqueness race reaches its publish and stops the engine — so it is a measurement switch, never a configuration one. Kept as a
    /// flag on the same binary because that is the only way to compare two builds of this code honestly: a second build moves the JIT's
    /// decisions and the layout of everything around it.
    /// </remarks>
    internal bool SkipClaimsForTest { get; set; }

    /// <summary>Claims taken since the last reset, so a timing has a denominator (<see cref="ResetCountersForTest"/>).</summary>
    internal long ClaimsTaken;

    /// <summary>
    /// Claims <paramref name="key"/> in <paramref name="index"/> for the calling commit, or reports that another in-flight commit holds it.
    /// </summary>
    /// <returns><see langword="true"/> when the claim is now this commit's; <see langword="false"/> when another commit holds it.</returns>
    internal bool TryClaim(IBTreeIndex index, byte* key)
    {
        if (SkipClaimsForTest)
        {
            return true;
        }

        System.Threading.Interlocked.Increment(ref ClaimsTaken);
        var reference = new UniqueKeyRef(index, key);
        lock (_gate)
        {
            return _held.Add(reference) || AdmitClaimedKeysForTest;
        }
    }

    /// <summary>Releases one claim. Taking the same key twice in one commit is a violation, so a claim is released exactly once.</summary>
    /// <remarks>
    /// Released by the reference the claim was taken with rather than by re-deriving one from the pointer, so a release cannot miss its own entry because
    /// the bytes behind the pointer moved — and so a release costs no second hash of the key.
    /// </remarks>
    internal void Release(in UniqueKeyRef claim)
    {
        if (SkipClaimsForTest)
        {
            return;
        }

        lock (_gate)
        {
            _held.Remove(claim);
        }
    }
}

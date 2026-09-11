using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// Per-thread free list of DFS traversal stacks for the R-Tree query enumerators (#916 O1).
/// </summary>
/// <remarks>
/// <para><b>What it replaces, and why the inline version cost what it did.</b> The stack used to be a <c>[InlineArray(256)]</c> field embedded by value in
/// <see cref="SpatialRTree{TStore}.AABBQueryEnumerator"/>, which is itself embedded by value in <c>AabbClusterEnumerator</c>. So every spatial query
/// constructed, zeroed and copied <b>1 KB</b> of traversal stack — for a per-cell tree that tightness-gated promotion (step 16) essentially never builds at
/// the extents the engine produces. Ablation measured it at 18 ns of a 69.9 ns query setup, −26 %: the largest single identified component.</para>
/// <para><b>Why <see cref="ThreadStaticAttribute"/> is sound here, and the hazard it creates.</b> This is the idiom
/// <c>ArchetypeClusterState.CandidateScratch</c> uses and for the same reasons — one buffer per worker, capacity fixed, never trimmed. But a scratch
/// buffer shared by a <c>ref struct</c> enumerator is a different problem from one shared by a method: <b>two enumerators can be live on one thread</b>.
/// Nested spatial queries are entirely plausible — query A, and for each hit query B — and with the inline array they were safe by construction because each
/// enumerator owned its own stack. A single shared buffer would make that silently wrong, which is <c>SQ-05</c>'s <c>[silent]</c> direction. Hence a free
/// LIST rather than a single buffer: every live enumerator holds a distinct array, and nesting depth is bounded only by memory.</para>
/// <para><b>Buffers are returned dirty.</b> Nothing clears them, and nothing should: the DFS protocol writes a slot before it reads it and
/// <c>_stackTop</c> is the only thing that says which slots are live. Zeroing on rent or return would reintroduce the 1 KB memset this exists to
/// remove.</para>
/// </remarks>
internal static class QueryStackPool
{
    /// <summary>
    /// Slots in one traversal stack — the depth bound <c>SQ-05</c> states, unchanged from the inline array it replaces.
    /// </summary>
    /// <remarks>
    /// It is a hard ceiling, not a hint: <c>PushChild</c> drops a child rather than grow, and records the drop through
    /// <see cref="SpatialRTreeDiagnostics.RecordDfsStackOverflow"/>, because the push happens under an OLC read latch where throwing is not an option.
    /// Growing the buffer here instead would turn a bounded, reported degradation into an unbounded allocation on the query path.
    /// </remarks>
    internal const int Capacity = 256;

    /// <summary>
    /// Index of the ownership token each buffer carries past its DFS slots — the buffers are <c>Capacity + 1</c> ints long.
    /// </summary>
    /// <remarks>
    /// <b>Identity alone cannot make a return safe, which is why this exists.</b> The obvious guard — scan the free list and ignore a buffer already in it —
    /// only catches a double return while the buffer is still parked. It does not catch the case that matters: a copy of an enumerator that has ALREADY
    /// rented (<c>GetEnumerator()</c> returns <c>this</c>) disposes and returns the buffer, another query rents it, and then the original disposes and
    /// returns it a second time. The scan finds nothing — the buffer is legitimately out on loan — so it is parked while still live, and the next rent
    /// hands one traversal stack to two enumerators. Silent subset results, which is <c>SQ-01</c> reached through <c>SQ-05</c>.
    /// <para>The token closes it. Every rent stamps a fresh value into the buffer and hands the same value to the caller; a return is accepted only when the
    /// two still agree, and accepting one zeroes the stamp. A stale copy therefore fails on the zero, and a copy stale across a re-rent fails on the new
    /// value. No in-repo caller does this today, but <c>AabbClusterEnumerator</c> is public and reaches game code through
    /// <see cref="ClusterSpatialQuery{TArch}"/>, so "no caller does that" is not a property this can rest on.</para>
    /// </remarks>
    private const int TokenSlot = Capacity;

    /// <summary>
    /// Buffers one thread keeps for reuse. Eight is the nesting depth this retains for free; a ninth concurrently-live enumerator still works — its buffer is
    /// allocated on rent and dropped on return rather than pooled.
    /// </summary>
    private const int MaxRetained = 8;

    [ThreadStatic]
    private static int[][] Free;

    [ThreadStatic]
    private static int FreeCount;

    /// <summary>Source of ownership tokens for this thread. Never 0 — that value marks a buffer nobody owns.</summary>
    [ThreadStatic]
    private static int TokenSeq;

    /// <summary>Take a stack for one enumerator. The caller owns it, with <paramref name="token"/> as its proof, until it calls <see cref="Return"/>.</summary>
    internal static int[] Rent(out int token)
    {
        var free = Free;
        int[] buffer;
        if (free != null && FreeCount > 0)
        {
            var index = --FreeCount;
            buffer = free[index];
            free[index] = null;   // drop the pool's reference so an abandoned enumerator cannot alias a rented buffer
        }
        else
        {
            buffer = new int[Capacity + 1];
        }

        token = ++TokenSeq;
        if (token == 0)
        {
            token = ++TokenSeq;   // wrapped after 2^32 rents on one thread; 0 means "unowned" and must never be issued
        }

        buffer[TokenSlot] = token;
        return buffer;
    }

    /// <summary>
    /// Give a stack back. Safe with <c>null</c>, with a foreign array, and with a token that no longer owns the buffer — all are ignored.
    /// </summary>
    /// <remarks>
    /// The token check is what makes a stale return harmless; see <see cref="TokenSlot"/> for the case it exists to stop and why an identity scan does not
    /// stop it. Zeroing the stamp on the way out is the half that makes the guard hold across a re-rent.
    /// </remarks>
    internal static void Return(int[] buffer, int token)
    {
        if (buffer == null || token == 0 || buffer.Length != Capacity + 1 || buffer[TokenSlot] != token)
        {
            return;
        }

        buffer[TokenSlot] = 0;   // every other copy holding this (buffer, token) pair now fails the check above

        var free = Free ??= new int[MaxRetained][];
        if (FreeCount >= MaxRetained)
        {
            return;
        }

        free[FreeCount++] = buffer;
    }

    /// <summary>Drop this thread's retained buffers, so a test starts from a known free list rather than from whatever ran before it.</summary>
    /// <remarks>
    /// Test helper (internal for <c>InternalsVisibleTo</c>). Without it the pool's identity assertions are order-dependent: a fixture that leaves eight
    /// buffers retained makes the next test's <see cref="Return"/> a no-op, and the assertion then passes for the wrong reason.
    /// </remarks>
    internal static void ResetForTests()
    {
        Free = null;
        FreeCount = 0;
    }
}

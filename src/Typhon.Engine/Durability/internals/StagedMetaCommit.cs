using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// A bootstrap mutation that must become durable in the same meta flip as a checkpoint cycle's CheckpointLSN advance — the commit record of a schema
/// migration (SCHEMA-09, #1469 / #1473). Handed to <see cref="CheckpointManager.StageMetaCommit"/>; the next cycle that advances CheckpointLSN runs
/// <see cref="Mutate"/> inside its watermark write.
/// </summary>
/// <remarks>
/// <para>
/// The cycle takes it at its START, before the metadata hook and the dirty-page collection: every page dirtied before the commit was staged is then one this
/// cycle writes before its flip. A cycle that stops short of the flip (gated, deferred, failed) puts it back for the next one.
/// </para>
/// <para>
/// <see cref="TryCommit"/> and <see cref="Cancel"/> exclude each other, so the stager that gives up learns the outcome exactly: either its cancel landed and
/// no flip will carry the mutation, or the flip already has. <see cref="Mutate"/> must be idempotent — a flip that failed after it ran is retried.
/// </para>
/// </remarks>
internal sealed class StagedMetaCommit
{
    private readonly object _gate = new();
    private volatile bool _committed;
    private bool _cancelled;

    public StagedMetaCommit(Action mutate, Action undo)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        ArgumentNullException.ThrowIfNull(undo);
        Mutate = mutate;
        Undo = undo;
    }

    /// <summary>Applied to the in-memory bootstrap under the meta lock, in the flip that advances CheckpointLSN.</summary>
    public Action Mutate { get; }

    /// <summary>
    /// Reverts <see cref="Mutate"/> in the in-memory bootstrap, under the same lock, when the flip's write fails: left there, the next meta write of any kind
    /// would make the commit durable over a CheckpointLSN that never moved.
    /// </summary>
    public Action Undo { get; }

    /// <summary>Set once the flip carrying <see cref="Mutate"/> is durable.</summary>
    public bool Committed => _committed;

    /// <summary>Runs <paramref name="flip"/> — the watermark write carrying <see cref="Mutate"/> — unless the commit was cancelled; true when it ran.</summary>
    internal bool TryCommit(Action flip)
    {
        lock (_gate)
        {
            if (_cancelled)
            {
                return false;
            }

            flip();
            _committed = true;
            return true;
        }
    }

    /// <summary>Cancels the commit; false when a flip has already made it durable, and the stager must treat it as committed.</summary>
    internal bool Cancel()
    {
        lock (_gate)
        {
            if (_committed)
            {
                return false;
            }

            _cancelled = true;
            return true;
        }
    }

    /// <summary>Whether the commit is still waiting for a flip.</summary>
    internal bool IsOutstanding
    {
        get
        {
            lock (_gate)
            {
                return !_committed && !_cancelled;
            }
        }
    }
}

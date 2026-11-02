using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// Whether the next WAL segment of a recovery scan continues the log. The one rule both scanners apply (<see cref="WalRecovery"/>, <see cref="RecoveryDriver"/>):
/// the frontier and the replay must stop at the same place (LOG-03).
/// </summary>
/// <remarks>
/// <para>
/// <b>A damaged header</b> is a file with no valid header below one that has it. Spares and placeholders are always above the last written segment, and a reopen
/// deletes those before it creates anything higher, so such a file at the top holds nothing, and below a valid one it held records. Unless the segment above starts
/// at or below CheckpointLSN: what the damaged file held is in the data file, and a floor that keeps such segments keeps them for the archive, not for recovery.
/// </para>
/// <para>
/// <b>A missing segment</b> shows in the header of the one after it. A rotation writes the last LSN of the segment it seals into <see cref="WalSegmentHeader.PrevSegmentLSN"/>,
/// so a <c>PrevSegmentLSN</c> above <c>max(highest LSN read, CheckpointLSN)</c> names records nobody read. <see cref="WalSegmentHeader.FirstLSN"/> cannot be the
/// check: a new lifecycle starts it from the frontier its open read, which can sit past what a later reader finds, a torn frame's valid chunk counting for the
/// first and sealed away from the second. A false stop drops acknowledged commits, so a lifecycle's first segment, whose <c>PrevSegmentLSN</c> is 0, is never one:
/// the gap before it goes unseen. Nor can ids be the check: they legally skip, a reopen removing spares and header-only segments and continuing above the highest
/// id it saw.
/// </para>
/// </remarks>
internal struct WalSegmentWalk(long checkpointLsn)
{
    private readonly long _checkpointLsn = checkpointLsn;
    private long _highest = checkpointLsn;
    private bool _invalidSeen;

    /// <summary>A file with no valid header was passed.</summary>
    public void NoteInvalidHeader() => _invalidSeen = true;

    /// <summary>A segment was read through this LSN.</summary>
    public void NoteRead(long highestLsn) => _highest = Math.Max(_highest, highestLsn);

    /// <summary>Whether a segment with this header breaks the log, and how.</summary>
    public WalRecoveryBreak Check(in WalSegmentHeader header)
    {
        if (_invalidSeen && header.FirstLSN - 1 > _checkpointLsn)
        {
            return WalRecoveryBreak.DamagedHeader;
        }

        _invalidSeen = false;
        return header.PrevSegmentLSN > _highest ? WalRecoveryBreak.MissingSegment : WalRecoveryBreak.None;
    }
}

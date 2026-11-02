using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>LOG-03's one rule for both scanners. A false stop drops acknowledged commits, so the arms that must NOT stop matter as much as the ones that must.</summary>
[TestFixture]
internal sealed class WalSegmentWalkTests
{
    private const long Checkpoint = 100;

    private static WalSegmentHeader Header(long firstLsn, long prevLsn) => new() { FirstLSN = firstLsn, PrevSegmentLSN = prevLsn };

    private static WalRecoveryBreak CheckAfterRead(long readThrough, in WalSegmentHeader header)
    {
        var walk = new WalSegmentWalk(Checkpoint);
        walk.NoteRead(readThrough);
        return walk.Check(in header);
    }

    /// <summary>The false stop a FirstLSN chain had: a lifecycle starts from a frontier that counted a torn frame's chunk, which no later reader finds.</summary>
    [Test]
    [VerifiesRule("LOG-03")]
    public void ALifecyclesFirstSegment_NeverBreaksTheLog() => Assert.That(CheckAfterRead(150, Header(5_000, 0)), Is.EqualTo(WalRecoveryBreak.None));

    [Test]
    [VerifiesRule("LOG-03")]
    public void APreviousLsnPastWhatWasRead_IsAMissingSegment() => Assert.That(CheckAfterRead(150, Header(201, 200)), Is.EqualTo(WalRecoveryBreak.MissingSegment));

    [Test]
    [VerifiesRule("LOG-03")]
    public void APreviousLsnAtWhatWasRead_ContinuesTheLog() => Assert.That(CheckAfterRead(150, Header(151, 150)), Is.EqualTo(WalRecoveryBreak.None));

    /// <summary>A reclaimed segment's records are in the data file: the first one left names a previous LSN nothing read.</summary>
    [Test]
    [VerifiesRule("LOG-03")]
    public void APreviousLsnBelowTheCheckpoint_ContinuesTheLog()
    {
        var header = Header(91, 90);
        Assert.That(new WalSegmentWalk(Checkpoint).Check(in header), Is.EqualTo(WalRecoveryBreak.None));
    }

    [Test]
    [VerifiesRule("LOG-03")]
    public void ADamagedHeader_BreaksTheLogUnderASegmentPastTheCheckpoint()
    {
        var walk = new WalSegmentWalk(Checkpoint);
        walk.NoteInvalidHeader();
        var header = Header(Checkpoint + 2, 0);
        Assert.That(walk.Check(in header), Is.EqualTo(WalRecoveryBreak.DamagedHeader));
    }

    /// <summary>What a damaged file held is in the data file when the segment above it starts at the checkpoint: the floor kept it for the archive, not for recovery.</summary>
    [Test]
    [VerifiesRule("LOG-03")]
    public void ADamagedHeader_BelowTheCheckpoint_IsForgotten()
    {
        var walk = new WalSegmentWalk(Checkpoint);
        walk.NoteInvalidHeader();
        var consolidated = Header(Checkpoint + 1, 0);
        var later = Header(5_000, 0);

        Assert.That(walk.Check(in consolidated), Is.EqualTo(WalRecoveryBreak.None));
        Assert.That(walk.Check(in later), Is.EqualTo(WalRecoveryBreak.None), "the damage does not stay armed for the next segment");
    }
}

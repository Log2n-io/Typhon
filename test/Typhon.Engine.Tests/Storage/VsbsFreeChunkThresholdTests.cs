using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// #1210: a value buffer counts its parked free chunks in a 16-bit field. The threshold that decides parking against freeing grew with the buffer's
/// elements, unclamped, so past ~8.1 M elements under one key the count wrapped negative: emptied chunks were no longer freed, and a crash open took the
/// buffer's root for no root and skipped its lock reset.
/// </summary>
[TestFixture]
class VsbsFreeChunkThresholdTests
{
    [Test]
    [VerifiesRule("IXW-08")]
    public void TheParkingThreshold_StaysBelowTheCountsWidth([Values(0, 1_000, 8_200_000, 100_000_000, int.MaxValue)] int totalCount,
        [Values(14, 56, 62)] int elementsPerChunk)
    {
        var threshold = VariableSizedBufferRecovery.FreeChunkThreshold(totalCount, elementsPerChunk);

        // A chunk is parked only while the count is at or below the threshold, so the count reaches at most threshold + 1.
        Assert.That(threshold + 1, Is.LessThanOrEqualTo(short.MaxValue), "a parked count that cannot wrap its 16 bits");
        Assert.That(threshold, Is.GreaterThanOrEqualTo(8), "the floor a small buffer keeps");
    }

    [Test]
    [VerifiesRule("IXW-08")]
    public void TheParkingThreshold_StillScalesWithTheBuffer()
    {
        Assert.That(VariableSizedBufferRecovery.FreeChunkThreshold(62 * 4 * 1_000, 62), Is.EqualTo(1_000), "a quarter of the elements' worth of chunks");
    }
}

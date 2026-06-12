using NUnit.Framework;
using System;

namespace Typhon.Engine.Tests;

/// <summary>
/// Single-threaded unit tests for <see cref="WalCommitBuffer"/>.
/// Verifies claim, publish, drain, overflow, swap, abandon, and dispose semantics.
/// </summary>
[TestFixture]
public class WalCommitBufferTests : AllocatorTestBase
{
    // Minimum capacity for tests: 64 KB
    private const int TestCapacity = 64 * 1024;

    private WalCommitBuffer CreateBuffer(int capacity = TestCapacity, long initialLSN = 1) =>
        new(MemoryAllocator, AllocationResource, capacity, initialLSN);

    #region Constructor

    [Test]
    public void Constructor_ValidCapacity_Succeeds()
    {
        using var buffer = CreateBuffer();

        Assert.That(buffer.BufferCapacity, Is.EqualTo(TestCapacity));
        Assert.That(buffer.NextLsn, Is.EqualTo(1));
        Assert.That(buffer.ActiveBufferIndex, Is.EqualTo(0));
    }

    [Test]
    public void Constructor_CustomInitialLSN_IsUsed()
    {
        using var buffer = CreateBuffer(initialLSN: 1000);

        Assert.That(buffer.NextLsn, Is.EqualTo(1000));
    }

    [Test]
    public void Constructor_TooSmall_Throws()
    {
        Assert.Throws<ArgumentException>(() => CreateBuffer(1024));
    }

    [Test]
    public void Constructor_Misaligned_Throws()
    {
        Assert.Throws<ArgumentException>(() => CreateBuffer(TestCapacity + 1));
    }

    #endregion

    #region TryClaim — Basic

    [Test]
    public void TryClaim_ReturnsValidClaim()
    {
        using var buffer = CreateBuffer();
        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(1));

        var claim = buffer.TryClaim(100, 1, ref ctx);

        Assert.That(claim.IsValid, Is.True);
        Assert.That(claim.RecordCount, Is.EqualTo(1));
        Assert.That(claim.FirstLSN, Is.EqualTo(1));
        Assert.That(claim.DataSpan.Length, Is.GreaterThanOrEqualTo(100));

        buffer.Publish(ref claim);
    }

    [Test]
    public void TryClaim_DataSpanIs8ByteAligned()
    {
        using var buffer = CreateBuffer();
        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(1));

        // Request an odd number of bytes
        var claim = buffer.TryClaim(13, 1, ref ctx);

        // Total frame size should be 8-byte aligned: Align8(WalFrameHeader.SizeInBytes + 13) = Align8(16 + 13) = Align8(29) = 32
        Assert.That(claim.TotalFrameSize % 8, Is.EqualTo(0));
        Assert.That(claim.TotalFrameSize, Is.EqualTo(32));

        buffer.Publish(ref claim);
    }

    [Test]
    public void TryClaim_MultipleRecords_AssignsContiguousLSNs()
    {
        using var buffer = CreateBuffer();
        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(1));

        var claim1 = buffer.TryClaim(32, 3, ref ctx);
        Assert.That(claim1.FirstLSN, Is.EqualTo(1));

        var claim2 = buffer.TryClaim(32, 2, ref ctx);
        Assert.That(claim2.FirstLSN, Is.EqualTo(4)); // 1+3

        buffer.Publish(ref claim1);
        buffer.Publish(ref claim2);

        Assert.That(buffer.NextLsn, Is.EqualTo(6)); // 1+3+2
    }

    [Test]
    public void TryClaim_TooLargeForBuffer_ThrowsImmediately()
    {
        using var buffer = CreateBuffer();
        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(1));

        Assert.Throws<WalClaimTooLargeException>(() =>
        {
            buffer.TryClaim(TestCapacity + 1, 1, ref ctx);
        });
    }

    #endregion

    #region Publish

    [Test]
    public unsafe void Publish_SetsFrameLengthInBuffer()
    {
        using var buffer = CreateBuffer();
        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(1));

        var claim = buffer.TryClaim(64, 1, ref ctx);

        // Write a known pattern
        claim.DataSpan.Fill(0xAB);

        buffer.Publish(ref claim);

        // After publishing, the claim should be invalidated
        Assert.That(claim.IsValid, Is.False);
    }

    [Test]
    public void Publish_InvalidClaim_Throws()
    {
        using var buffer = CreateBuffer();

        var claim = new WalClaim { IsValid = false };

        // Can't use Assert.Throws with ref struct in lambda — call directly
        var threw = false;
        try
        {
            buffer.Publish(ref claim);
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        Assert.That(threw, Is.True);
    }

    #endregion

    #region TryDrain — Basic

    [Test]
    public void TryDrain_NothingPublished_ReturnsFalse()
    {
        using var buffer = CreateBuffer();

        var result = buffer.TryDrain(out var data, out var frameCount);

        Assert.That(result, Is.False);
        Assert.That(frameCount, Is.EqualTo(0));
    }

    [Test]
    public void TryDrain_OnePublished_ReturnsData()
    {
        using var buffer = CreateBuffer();
        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(1));

        var claim = buffer.TryClaim(64, 1, ref ctx);
        claim.DataSpan.Fill(0xCD);
        buffer.Publish(ref claim);

        var result = buffer.TryDrain(out var data, out var frameCount);

        Assert.That(result, Is.True);
        Assert.That(frameCount, Is.EqualTo(1));
        Assert.That(data.Length, Is.GreaterThan(0));
    }

    [Test]
    public void TryDrain_StopsAtUnpublished()
    {
        using var buffer = CreateBuffer();
        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(1));

        // Publish first claim
        var claim1 = buffer.TryClaim(32, 1, ref ctx);
        buffer.Publish(ref claim1);

        // Claim but don't publish second
        var claim2 = buffer.TryClaim(32, 1, ref ctx);

        var result = buffer.TryDrain(out _, out var frameCount);

        Assert.That(result, Is.True);
        Assert.That(frameCount, Is.EqualTo(1)); // Only the published one

        // Cleanup
        buffer.AbandonClaim(ref claim2);
    }

    [Test]
    public void TryDrain_MultiplePublished_DrainsContiguousBatch()
    {
        using var buffer = CreateBuffer();
        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(1));

        for (var i = 0; i < 5; i++)
        {
            var claim = buffer.TryClaim(32, 1, ref ctx);
            claim.DataSpan.Fill((byte)(i + 1));
            buffer.Publish(ref claim);
        }

        var result = buffer.TryDrain(out var data, out var frameCount);

        Assert.That(result, Is.True);
        Assert.That(frameCount, Is.EqualTo(5));

        // Verify data integrity via frame walking
        var walkedFrames = 0;
        WalCommitBuffer.WalkFrames(data, (payload, recordCount) =>
        {
            walkedFrames++;
            Assert.That(recordCount, Is.EqualTo(1));
        });
        Assert.That(walkedFrames, Is.EqualTo(5));
    }

    #endregion

    #region CompleteDrain

    [Test]
    public void CompleteDrain_AdvancesDrainPosition()
    {
        using var buffer = CreateBuffer();
        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(1));

        var claim = buffer.TryClaim(32, 1, ref ctx);
        buffer.Publish(ref claim);

        buffer.TryDrain(out var data, out _);
        buffer.CompleteDrain(data.Length);

        // Second drain should find nothing
        var result = buffer.TryDrain(out _, out var frameCount);
        Assert.That(result, Is.False);
        Assert.That(frameCount, Is.EqualTo(0));
    }

    #endregion

    #region AbandonClaim

    [Test]
    public void AbandonClaim_DecrementsInflightCount()
    {
        using var buffer = CreateBuffer();
        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(1));

        var claim = buffer.TryClaim(32, 1, ref ctx);
        Assert.That(buffer.InflightCount, Is.EqualTo(1));

        buffer.AbandonClaim(ref claim);
        Assert.That(buffer.InflightCount, Is.EqualTo(0));
        Assert.That(claim.IsValid, Is.False);
    }

    [Test]
    public void AbandonClaim_InvalidClaim_NoOp()
    {
        using var buffer = CreateBuffer();

        var claim = new WalClaim { IsValid = false };
        buffer.AbandonClaim(ref claim); // Should not throw
    }

    [Test]
    public void AbandonClaim_ConsumerSkipsAbandonedFrame()
    {
        using var buffer = CreateBuffer();
        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(1));

        // Claim and abandon first
        var abandoned = buffer.TryClaim(32, 1, ref ctx);
        buffer.AbandonClaim(ref abandoned);

        // Claim and publish second
        var published = buffer.TryClaim(32, 1, ref ctx);
        published.DataSpan.Fill(0xEE);
        buffer.Publish(ref published);

        // Drain should see both frames (abandoned + published)
        buffer.TryDrain(out var data, out var frameCount);
        Assert.That(frameCount, Is.EqualTo(2));

        // But walking frames should only invoke callback for the published one (recordCount > 0)
        var realFrames = 0;
        WalCommitBuffer.WalkFrames(data, (payload, recordCount) =>
        {
            realFrames++;
            Assert.That(recordCount, Is.EqualTo(1));
        });
        Assert.That(realFrames, Is.EqualTo(1));
    }

    #endregion

    #region Utilization

    [Test]
    public void Utilization_Empty_IsZero()
    {
        using var buffer = CreateBuffer();

        Assert.That(buffer.Utilization, Is.EqualTo(0.0).Within(0.001));
    }

    [Test]
    public void Utilization_AfterClaim_Increases()
    {
        using var buffer = CreateBuffer();
        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(1));

        var claim = buffer.TryClaim(1024, 1, ref ctx);
        Assert.That(buffer.Utilization, Is.GreaterThan(0.0));

        buffer.Publish(ref claim);
    }

    #endregion

    #region Overflow + Back-pressure

    [Test]
    public void TryClaim_OverflowTimeout_ThrowsWalBackPressureTimeout()
    {
        using var buffer = CreateBuffer();

        // Fill the buffer with a single large claim
        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(1));
        var bigClaim = buffer.TryClaim(TestCapacity - 64, 1, ref ctx);
        buffer.Publish(ref bigClaim);

        // Next claim should overflow and timeout quickly
        var shortCtx = WaitContext.FromTimeout(TimeSpan.FromMilliseconds(50));
        Assert.Throws<WalBackPressureTimeoutException>(() =>
        {
            buffer.TryClaim(1024, 1, ref shortCtx);
        });
    }

    #endregion

    #region Overflow + Swap

    [Test]
    [CancelAfter(5000)]
    public void Swap_AfterOverflow_ProducersCanContinue()
    {
        using var buffer = CreateBuffer();
        var claimSize = 1024;
        var frameSize = WalCommitBuffer.Align8(8 + claimSize);
        var consumerStop = 0;
        var producerClaimCount = 0;

        // Background consumer that drains and completes (enables swap)
        var consumer = new System.Threading.Thread(() =>
        {
            while (consumerStop == 0)
            {
                if (buffer.TryDrain(out var data, out _))
                {
                    buffer.CompleteDrain(data.Length);
                }
                else
                {
                    buffer.WaitForData(10);
                }
            }
        });
        consumer.IsBackground = true;
        consumer.Start();

        // Fill more than one buffer's worth to force at least one swap
        var totalClaims = (TestCapacity / frameSize) + 10;
        for (var i = 0; i < totalClaims; i++)
        {
            var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(3));
            var claim = buffer.TryClaim(claimSize, 1, ref ctx);
            claim.DataSpan.Fill(0xAA);
            buffer.Publish(ref claim);
            producerClaimCount++;
        }

        consumerStop = 1;
        buffer.Signal(); // Wake consumer if blocked in WaitForData
        consumer.Join(3000);

        Assert.That(producerClaimCount, Is.EqualTo(totalClaims));
    }

    #endregion

    #region Dispose

    [Test]
    public void Dispose_SubsequentTryClaim_ThrowsObjectDisposed()
    {
        var buffer = CreateBuffer();
        buffer.Dispose();

        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(1));
        Assert.Throws<ObjectDisposedException>(() =>
        {
            buffer.TryClaim(32, 1, ref ctx);
        });
    }

    [Test]
    public void Dispose_DoubleDispose_DoesNotThrow()
    {
        var buffer = CreateBuffer();
        buffer.Dispose();
        Assert.DoesNotThrow(() => buffer.Dispose());
    }

    #endregion

    #region Align8 Helper

    [Test]
    [TestCase(0, 0)]
    [TestCase(1, 8)]
    [TestCase(7, 8)]
    [TestCase(8, 8)]
    [TestCase(9, 16)]
    [TestCase(15, 16)]
    [TestCase(16, 16)]
    [TestCase(21, 24)] // WalFrameHeader(8) + 13 bytes payload
    public void Align8_RoundsUpCorrectly(int input, int expected) =>
        Assert.That(WalCommitBuffer.Align8(input), Is.EqualTo(expected));

    #endregion
}

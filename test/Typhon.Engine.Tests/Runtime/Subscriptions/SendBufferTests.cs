using NUnit.Framework;
using System;

namespace Typhon.Engine.Tests;

[TestFixture]
public class SendBufferTests
{
    [Test]
    public void NewBuffer_IsEmpty()
    {
        using var buffer = new SendBuffer(1024);

        Assert.That(buffer.IsEmpty, Is.True);
        Assert.That(buffer.PendingBytes, Is.EqualTo(0));
        Assert.That(buffer.FillPercentage, Is.EqualTo(0f).Within(0.001f));
    }

    [Test]
    public void TryWrite_SmallData_Succeeds()
    {
        using var buffer = new SendBuffer(1024);
        var data = new byte[] { 1, 2, 3, 4, 5 };

        var result = buffer.TryWrite(data);

        Assert.That(result, Is.True);
        Assert.That(buffer.PendingBytes, Is.EqualTo(5));
        Assert.That(buffer.IsEmpty, Is.False);
    }

    [Test]
    public void TryWrite_ExceedsCapacity_Fails()
    {
        using var buffer = new SendBuffer(16);
        var data = new byte[20]; // Exceeds capacity

        var result = buffer.TryWrite(data);

        Assert.That(result, Is.False);
        Assert.That(buffer.IsEmpty, Is.True);
    }

    [Test]
    public void GetReadSpan_ReturnsWrittenData()
    {
        using var buffer = new SendBuffer(1024);
        var data = new byte[] { 10, 20, 30 };
        buffer.TryWrite(data);

        var span = buffer.GetReadSpan();

        Assert.That(span.Length, Is.EqualTo(3));
        Assert.That(span[0], Is.EqualTo(10));
        Assert.That(span[1], Is.EqualTo(20));
        Assert.That(span[2], Is.EqualTo(30));
    }

    [Test]
    public void AdvanceRead_FreesSpace()
    {
        using var buffer = new SendBuffer(1024);
        buffer.TryWrite(new byte[] { 1, 2, 3, 4, 5 });

        buffer.AdvanceRead(3);

        Assert.That(buffer.PendingBytes, Is.EqualTo(2));
    }

    [Test]
    public void WrapAround_ReadWrite_Works()
    {
        using var buffer = new SendBuffer(16);

        // Write 10 bytes
        buffer.TryWrite(new byte[10]);
        // Read 10 bytes (head advances to 10)
        buffer.AdvanceRead(10);

        // Now write 10 more bytes — wraps around
        var data = new byte[] { 100, 101, 102, 103, 104, 105, 106, 107, 108, 109 };
        var result = buffer.TryWrite(data);

        Assert.That(result, Is.True);
        Assert.That(buffer.PendingBytes, Is.EqualTo(10));

        // Read first chunk (from position 10 to end = 6 bytes)
        var span1 = buffer.GetReadSpan();
        Assert.That(span1.Length, Is.EqualTo(6));
        Assert.That(span1[0], Is.EqualTo(100));
        buffer.AdvanceRead(6);

        // Read second chunk (from position 0 = 4 bytes)
        var span2 = buffer.GetReadSpan();
        Assert.That(span2.Length, Is.EqualTo(4));
        Assert.That(span2[0], Is.EqualTo(106));
        buffer.AdvanceRead(4);

        Assert.That(buffer.IsEmpty, Is.True);
    }

    [Test]
    public void FillPercentage_CorrectAtVariousLevels()
    {
        using var buffer = new SendBuffer(100);

        buffer.TryWrite(new byte[25]);
        Assert.That(buffer.FillPercentage, Is.EqualTo(0.25f).Within(0.01f));

        buffer.TryWrite(new byte[50]);
        Assert.That(buffer.FillPercentage, Is.EqualTo(0.75f).Within(0.01f));
    }

    [Test]
    public void Reset_ClearsBuffer()
    {
        using var buffer = new SendBuffer(1024);
        buffer.TryWrite(new byte[100]);

        buffer.Reset();

        Assert.That(buffer.IsEmpty, Is.True);
        Assert.That(buffer.PendingBytes, Is.EqualTo(0));
    }

    [Test]
    public void TryWrite_EmptyData_Succeeds()
    {
        using var buffer = new SendBuffer(1024);

        var result = buffer.TryWrite(ReadOnlySpan<byte>.Empty);

        Assert.That(result, Is.True);
        Assert.That(buffer.IsEmpty, Is.True);
    }
}

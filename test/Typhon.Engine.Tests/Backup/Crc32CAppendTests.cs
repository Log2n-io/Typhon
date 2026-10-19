using NUnit.Framework;
using System;

namespace Typhon.Engine.Tests.Backup;

/// <summary>
/// The incremental CRC32C a pack's <c>FileCRC</c> and every tail segment's <c>RawCrc</c> are accumulated with: any split of a stream must give the
/// one-shot value, or a pack written in 4 MiB buffers would never verify.
/// </summary>
[TestFixture]
internal sealed class Crc32CAppendTests
{
    [Test]
    [CancelAfter(5_000)]
    public void EverySplitOfABuffer_GivesTheWholeBufferCrc()
    {
        var random = TestSeed.Random(nameof(EverySplitOfABuffer_GivesTheWholeBufferCrc));
        for (var round = 0; round < 64; round++)
        {
            var data = new byte[random.Next(0, 20_000)];
            random.NextBytes(data);
            var expected = Crc32CUtil.Compute(data);

            var state = Crc32CUtil.Start;
            var pos = 0;
            while (pos < data.Length)
            {
                // Odd sizes on purpose: the 8-byte vector loop and its byte tail must both be crossed at every alignment.
                var n = Math.Min(data.Length - pos, random.Next(0, 37));
                state = Crc32CUtil.Append(state, data.AsSpan(pos, n));
                pos += n;
            }

            Assert.That(Crc32CUtil.Finish(state), Is.EqualTo(expected), $"round {round}, {data.Length} bytes. {TestSeed.Repro}");
        }
    }

    [Test]
    [CancelAfter(5_000)]
    public void EmptyAppends_ChangeNothing()
    {
        var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var state = Crc32CUtil.Append(Crc32CUtil.Start, []);
        state = Crc32CUtil.Append(state, data.AsSpan(0, 4));
        state = Crc32CUtil.Append(state, []);
        state = Crc32CUtil.Append(state, data.AsSpan(4));

        Assert.That(Crc32CUtil.Finish(state), Is.EqualTo(Crc32CUtil.Compute(data)));
        Assert.That(Crc32CUtil.Finish(Crc32CUtil.Start), Is.EqualTo(Crc32CUtil.Compute([])));
    }
}

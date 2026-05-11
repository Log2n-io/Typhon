using System;
using System.Buffers.Binary;
using NUnit.Framework;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Round-trip tests for the v9 Query Definition Export events (#342). Covers:
/// <list type="bullet">
/// <item><see cref="QueryDefinitionDescribeEventCodec"/> at evaluator counts 0, 1, 4, 16.</item>
/// <item><see cref="QueryArgsEventCodec"/> at evaluator counts 0, 1, 4, 16.</item>
/// <item>Wire-level layout invariants — exact byte-by-byte sizes.</item>
/// </list>
/// </summary>
[TestFixture]
public class QueryDefinitionEventsRoundTripTests
{
    [Test]
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(4)]
    [TestCase(16)]
    public void QueryDefinitionDescribe_RoundTrips_WithVariousEvaluatorCounts(int evaluatorCount)
    {
        var evaluators = new byte[evaluatorCount * QueryDefinitionDescribeEventCodec.EvaluatorEntrySize];
        for (var i = 0; i < evaluatorCount; i++)
        {
            var off = i * QueryDefinitionDescribeEventCodec.EvaluatorEntrySize;
            BinaryPrimitives.WriteUInt16LittleEndian(evaluators.AsSpan(off), (ushort)(100 + i));
            evaluators[off + 2] = (byte)(i % 6);
            evaluators[off + 3] = 0;
        }
        var fieldDeps = new byte[evaluatorCount * QueryDefinitionDescribeEventCodec.FieldDependencyEntrySize];
        for (var i = 0; i < evaluatorCount; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(fieldDeps.AsSpan(i * 2), (ushort)(200 + i));
        }

        var expectedSize = QueryDefinitionDescribeEventCodec.ComputeSize(evaluatorCount, evaluatorCount);
        var buffer = new byte[expectedSize];

        QueryDefinitionDescribeEventCodec.Write(buffer, threadSlot: 7, timestamp: 12345, kind: 1, localId: 42,
            targetComponentType: 99, primaryIndexFieldIdx: -1, sortFieldIdx: 3, sortDescending: 1,
            definitionSourceFileId: 5, definitionSourceLine: 217, definitionSourceMethodId: 11,
            evaluators, fieldDeps, out var bytesWritten);

        Assert.That(bytesWritten, Is.EqualTo(expectedSize));

        var data = QueryDefinitionDescribeEventCodec.Read(buffer);
        Assert.That(data.ThreadSlot, Is.EqualTo(7));
        Assert.That(data.Timestamp, Is.EqualTo(12345));
        Assert.That(data.Kind, Is.EqualTo((byte)1));
        Assert.That(data.LocalId, Is.EqualTo((uint)42));
        Assert.That(data.TargetComponentType, Is.EqualTo((ushort)99));
        Assert.That(data.PrimaryIndexFieldIdx, Is.EqualTo((short)-1));
        Assert.That(data.SortFieldIdx, Is.EqualTo((short)3));
        Assert.That(data.SortDescending, Is.EqualTo((byte)1));
        Assert.That(data.DefinitionSourceFileId, Is.EqualTo((ushort)5));
        Assert.That(data.DefinitionSourceLine, Is.EqualTo(217));
        Assert.That(data.DefinitionSourceMethodId, Is.EqualTo((ushort)11));
        Assert.That(data.EvaluatorCount, Is.EqualTo((ushort)evaluatorCount));
        Assert.That(data.FieldDependencyCount, Is.EqualTo((ushort)evaluatorCount));

        // Verify evaluator contents
        var evSpan = data.EvaluatorBlob.Span;
        for (var i = 0; i < evaluatorCount; i++)
        {
            var off = i * QueryDefinitionDescribeEventCodec.EvaluatorEntrySize;
            Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(evSpan[off..]), Is.EqualTo((ushort)(100 + i)));
            Assert.That(evSpan[off + 2], Is.EqualTo((byte)(i % 6)));
        }

        // Verify field-dep contents
        var fdSpan = data.FieldDependenciesBlob.Span;
        for (var i = 0; i < evaluatorCount; i++)
        {
            Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(fdSpan[(i * 2)..]), Is.EqualTo((ushort)(200 + i)));
        }
    }

    [Test]
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(4)]
    [TestCase(16)]
    public void QueryArgs_RoundTrips_WithVariousEvaluatorCounts(int evaluatorCount)
    {
        var thresholds = new byte[evaluatorCount * QueryArgsEventCodec.ThresholdSize];
        for (var i = 0; i < evaluatorCount; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(thresholds.AsSpan(i * 8), 1_000_000L + i);
        }

        var expectedSize = QueryArgsEventCodec.ComputeSize(evaluatorCount);
        var buffer = new byte[expectedSize];

        QueryArgsEventCodec.Write(buffer, threadSlot: 3, timestamp: 99887766L, thresholds, out var bytesWritten);
        Assert.That(bytesWritten, Is.EqualTo(expectedSize));

        var data = QueryArgsEventCodec.Read(buffer);
        Assert.That(data.ThreadSlot, Is.EqualTo(3));
        Assert.That(data.Timestamp, Is.EqualTo(99887766L));
        Assert.That(data.EvaluatorCount, Is.EqualTo((ushort)evaluatorCount));
        for (var i = 0; i < evaluatorCount; i++)
        {
            Assert.That(data.GetThreshold(i), Is.EqualTo(1_000_000L + i));
        }
    }

    [Test]
    public void QueryDefinitionDescribe_SizeIsCorrect_For4Evaluators()
    {
        // FixedPrefixSize = 12 (common header) + 22 (fixed payload fields incl. EvaluatorCount) = 34
        // Then 4*4 (evaluators) + 2 (fieldDepCount) + 4*2 (fieldDeps) = 26
        // Total: 34 + 26 = 60 bytes
        var size = QueryDefinitionDescribeEventCodec.ComputeSize(4, 4);
        Assert.That(size, Is.EqualTo(60));
    }

    [Test]
    public void QueryArgs_SizeIsCorrect_For4Evaluators()
    {
        // 12 B common header + 2 B count + 4 * 8 B thresholds = 14 + 32 = 46 bytes
        var size = QueryArgsEventCodec.ComputeSize(4);
        Assert.That(size, Is.EqualTo(46));
    }

    [Test]
    public void QueryDefinitionDescribe_RejectsMalformedEvaluatorBlob()
    {
        // Length not a multiple of EvaluatorEntrySize (4) should throw.
        var bad = new byte[5];
        var ok = new byte[0];
        var buffer = new byte[64];

        Assert.That(() =>
            QueryDefinitionDescribeEventCodec.Write(buffer, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, bad, ok, out _),
            Throws.ArgumentException);
    }

    [Test]
    public void QueryArgs_RejectsMalformedThresholdsBlob()
    {
        var bad = new byte[7];
        var buffer = new byte[64];

        Assert.That(() => QueryArgsEventCodec.Write(buffer, 0, 0, bad, out _), Throws.ArgumentException);
    }
}

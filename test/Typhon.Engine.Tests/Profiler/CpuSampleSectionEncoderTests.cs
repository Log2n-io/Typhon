using System;
using System.Collections.Generic;
using NUnit.Framework;
using Typhon.Profiler;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Unit tests for <see cref="CpuSampleSectionEncoder"/> — the Phase 3 (#351) step that projects a parser-interned <see cref="ParsedCpuSamples"/> batch
/// into the wire form. Stacks / frames / sample ordering are interned by <see cref="CpuSampleParser"/>; the encoder's remaining job is folding frame file
/// paths into the shared FileTable and assigning frame symbol ids — those, plus the samples/stacks passthrough, are what is verified here.
/// </summary>
[TestFixture]
public sealed class CpuSampleSectionEncoderTests
{
    private static (List<string> fileTable, Dictionary<string, int> interner) EmptyFileTable() =>
        ([], new Dictionary<string, int>(StringComparer.Ordinal));

    [Test]
    public void FrameFilePaths_AppendedToSharedFileTable()
    {
        var fileTable = new List<string> { "/existing.cs" };
        var interner = new Dictionary<string, int>(StringComparer.Ordinal) { ["/existing.cs"] = 0 };
        var parsed = new ParsedCpuSamples(
            samples: [new CpuSampleRecord(1, 0, 0, 0), new CpuSampleRecord(2, 0, 0, 1)],
            stacks: [[0], [1]],
            frames: [new ParsedCpuFrame("A", "/existing.cs", 5), new ParsedCpuFrame("B", "/new.cs", 7)]);

        var data = CpuSampleSectionEncoder.Encode(parsed, fileTable, interner);

        Assert.That(fileTable, Is.EqualTo(new[] { "/existing.cs", "/new.cs" }), "new frame file paths append to the shared FileTable");
        Assert.That(data.FrameSymbols[0].FileId, Is.EqualTo((ushort)0), "the path already in the FileTable is reused");
        Assert.That(data.FrameSymbols[0].Line, Is.EqualTo((uint)5));
        Assert.That(data.FrameSymbols[1].FileId, Is.EqualTo((ushort)1), "the new path lands at the next FileTable index");
        Assert.That(data.FrameSymbols[1].Line, Is.EqualTo((uint)7));
    }

    [Test]
    public void FrameSymbols_AreDenseByFrameId()
    {
        var parsed = new ParsedCpuSamples(
            samples: [new CpuSampleRecord(1, 0, 0, 0)],
            stacks: [[0, 1, 2]],
            frames: [new ParsedCpuFrame("A", "/a.cs", 1), new ParsedCpuFrame("B", "/b.cs", 2), new ParsedCpuFrame("C", null, 0)]);
        var (fileTable, interner) = EmptyFileTable();

        var data = CpuSampleSectionEncoder.Encode(parsed, fileTable, interner);

        Assert.That(data.FrameSymbols.Count, Is.EqualTo(3));
        for (var i = 0; i < data.FrameSymbols.Count; i++)
        {
            Assert.That(data.FrameSymbols[i].FrameId, Is.EqualTo((ushort)i), "frame id == array index — the parser assigned a dense u16 space");
        }
    }

    [Test]
    public void NameOnlyFrame_GetsFileIdZeroAndLineZero()
    {
        var parsed = new ParsedCpuSamples(
            samples: [new CpuSampleRecord(1, 0, 0, 0)],
            stacks: [[0]],
            frames: [new ParsedCpuFrame("Native.X", filePath: null, line: 0)]);
        var (fileTable, interner) = EmptyFileTable();

        var data = CpuSampleSectionEncoder.Encode(parsed, fileTable, interner);

        Assert.That(data.FrameSymbols.Count, Is.EqualTo(1));
        Assert.That(data.FrameSymbols[0].FileId, Is.Zero);
        Assert.That(data.FrameSymbols[0].Line, Is.Zero);
        Assert.That(data.FrameSymbols[0].HasSource, Is.False);
        Assert.That(data.FrameSymbols[0].Method, Is.EqualTo("Native.X"), "a name-only frame keeps its method name");
    }

    [Test]
    public void SamplesAndStacks_PassThroughUnchanged()
    {
        // Sample ordering + stack interning are the parser's job — the encoder hands them straight to the wire.
        var samples = new[] { new CpuSampleRecord(50, 0, 0, 0), new CpuSampleRecord(100, 1, 1, 1) };
        var stacks = new[] { new ushort[] { 0, 1 }, new ushort[] { 1 } };
        var parsed = new ParsedCpuSamples(samples, stacks, [new ParsedCpuFrame("A", "/a.cs", 1), new ParsedCpuFrame("B", null, 0)]);
        var (fileTable, interner) = EmptyFileTable();

        var data = CpuSampleSectionEncoder.Encode(parsed, fileTable, interner);

        Assert.That(data.Samples, Is.SameAs(samples), "sample records pass through by reference — no copy");
        Assert.That(data.Stacks, Is.SameAs(stacks), "the interned stack table passes through by reference — no re-interning");
    }
}

using System;
using System.Collections.Generic;
using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// The interned form of a CPU-sample batch, ready for <c>TraceFileWriter.WriteCpuSampleSection</c>: sample records (each referencing a stack by index),
/// the interned stack table, and the interned frame-symbol table.
/// </summary>
internal sealed class CpuSampleSectionData
{
    public CpuSampleSectionData(IReadOnlyList<CpuSampleRecord> samples, IReadOnlyList<ushort[]> stacks, IReadOnlyList<CpuFrameSymbol> frameSymbols)
    {
        Samples = samples;
        Stacks = stacks;
        FrameSymbols = frameSymbols;
    }

    /// <summary>Sample records, sorted by <c>(threadSlot, qpc)</c>.</summary>
    public IReadOnlyList<CpuSampleRecord> Samples { get; }

    /// <summary>Interned stack table — each entry is a leaf-first array of frame ids.</summary>
    public IReadOnlyList<ushort[]> Stacks { get; }

    /// <summary>Interned frame symbols, dense by <c>FrameId</c>.</summary>
    public IReadOnlyList<CpuFrameSymbol> FrameSymbols { get; }
}

/// <summary>
/// Projects a parser-interned <see cref="ParsedCpuSamples"/> batch into the wire-shaped <see cref="CpuSampleSectionData"/> the <c>.typhon-trace</c>
/// CPU-sample trailer stores (#351, design §6.5). Stacks and frame symbols are <i>already interned</i> by <see cref="CpuSampleParser"/>; the only work
/// left here is folding each frame's file path into the shared <c>FileTable</c> (the same table the source-location manifest uses) and assigning the
/// resulting <c>fileId</c>. The sample records and stack table pass straight through — no copy, no re-interning.
/// </summary>
internal static class CpuSampleSectionEncoder
{
    /// <summary>
    /// Encodes <paramref name="parsed"/>. <paramref name="fileTable"/> / <paramref name="fileInterner"/> are the shared FileTable (seeded with the
    /// source-location manifest's files) — frame file paths are appended to both in place; the caller then writes the extended <paramref name="fileTable"/>.
    /// </summary>
    public static CpuSampleSectionData Encode(ParsedCpuSamples parsed, List<string> fileTable, Dictionary<string, int> fileInterner)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(fileTable);
        ArgumentNullException.ThrowIfNull(fileInterner);

        var frames = parsed.Frames;
        var frameSymbols = new CpuFrameSymbol[frames.Length];
        for (var i = 0; i < frames.Length; i++)
        {
            var f = frames[i];
            ushort fileId = 0;
            uint line = 0;
            if (f.FilePath != null)
            {
                fileId = InternFile(f.FilePath, fileTable, fileInterner);
                line = (uint)f.Line;
            }
            // Frame id == array index — the parser already assigned a dense u16 frame-id space.
            frameSymbols[i] = new CpuFrameSymbol((ushort)i, fileId, line, f.Method ?? string.Empty);
        }

        return new CpuSampleSectionData(parsed.Samples, parsed.Stacks, frameSymbols);
    }

    private static ushort InternFile(string path, List<string> fileTable, Dictionary<string, int> fileInterner)
    {
        if (fileInterner.TryGetValue(path, out var existing))
        {
            return (ushort)existing;
        }
        var id = fileTable.Count;
        fileTable.Add(path);
        fileInterner[path] = id;
        return (ushort)id;
    }
}

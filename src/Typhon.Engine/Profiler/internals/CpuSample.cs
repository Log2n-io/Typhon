using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// One <i>interned</i> frame symbol produced by <see cref="CpuSampleParser"/>. The parser resolves and de-duplicates frames as it walks the
/// <c>.nettrace</c>, so a frame appears once here regardless of how many sampled stacks reference it; stacks reference it by its index in
/// <see cref="ParsedCpuSamples.Frames"/>. BCL / native / dynamically-generated frames have no local PDB and resolve name-only —
/// <see cref="FilePath"/> is then null (the <c>siteId = 0</c> equivalent: the frame still renders, it just has no "Open in editor").
/// </summary>
internal readonly struct ParsedCpuFrame
{
    public ParsedCpuFrame(string method, string filePath, int line)
    {
        Method = method ?? string.Empty;
        FilePath = filePath;
        Line = line;
    }

    /// <summary>Display name of the frame's method (e.g. <c>Typhon.Engine.Foo.Bar</c>) — never null; <c>"?"</c> for fully unresolved native frames.</summary>
    public string Method { get; }

    /// <summary>Resolved source file path, or null when the module has no local portable PDB (BCL / native / dynamic).</summary>
    public string FilePath { get; }

    /// <summary>Resolved 1-based source line, or 0 when <see cref="FilePath"/> is null.</summary>
    public int Line { get; }
}

/// <summary>
/// The <i>already-interned</i> CPU-sample batch produced by <see cref="CpuSampleParser"/> (#351 Phase 2). Parsing interns directly: each unique
/// call stack (keyed on the TraceLog <c>CallStackIndex</c>) and each unique frame (keyed on the TraceLog <c>CodeAddressIndex</c>) is resolved once,
/// so memory is <c>O(uniqueStacks + uniqueFrames + samples)</c> — never the <c>O(samples × depth)</c> blow-up of materialising one resolved-frame
/// array per sample. A real session is millions of samples but only thousands of unique stacks / hundreds of unique frames.
/// </summary>
/// <remarks>
/// This is the seam between the parser and <see cref="CpuSampleSectionEncoder"/>: the encoder only has to fold <see cref="ParsedCpuFrame.FilePath"/>
/// strings into the shared <c>FileTable</c> — the <see cref="Samples"/> records and the <see cref="Stacks"/> table pass straight through to the wire,
/// no re-interning.
/// </remarks>
internal sealed class ParsedCpuSamples
{
    /// <summary>Sentinel for an absent / empty / unparsable capture.</summary>
    public static readonly ParsedCpuSamples Empty = new([], [], []);

    public ParsedCpuSamples(CpuSampleRecord[] samples, ushort[][] stacks, ParsedCpuFrame[] frames)
    {
        Samples = samples;
        Stacks = stacks;
        Frames = frames;
    }

    /// <summary>Sample records, sorted by <c>(threadSlot, qpc)</c>; each references a stack by <see cref="CpuSampleRecord.StackIndex"/>.</summary>
    public CpuSampleRecord[] Samples { get; }

    /// <summary>Interned stack table — each entry a leaf-first array of frame ids (indices into <see cref="Frames"/>).</summary>
    public ushort[][] Stacks { get; }

    /// <summary>Interned frame symbols — frame id = array index.</summary>
    public ParsedCpuFrame[] Frames { get; }

    /// <summary>Number of CPU samples in the batch.</summary>
    public int SampleCount => Samples.Length;
}

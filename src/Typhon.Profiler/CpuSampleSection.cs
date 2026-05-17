namespace Typhon.Profiler;

/// <summary>
/// One CPU stack sample in the trailing <c>CpuSampleSection</c> of a <c>.typhon-trace</c> file (#351). A sample is a single statistical capture of one
/// thread's call stack (~1 kHz); its call stack is stored once in the section's interned stack table and referenced here by <see cref="StackIndex"/>.
/// </summary>
/// <remarks>
/// On the wire <see cref="ThreadSlot"/> is a single byte with <c>0xFF</c> meaning "unslotted" (a non-Typhon thread — GC / thread-pool / finalizer /
/// sampler); the reader surfaces that as <c>-1</c>. Samples are stored sorted by <see cref="Qpc"/>, grouped per <see cref="ThreadSlot"/>.
/// </remarks>
public readonly struct CpuSampleRecord
{
    /// <summary>QPC timestamp of the sample, in the same time base as <c>TraceFileHeader.SamplingSessionStartQpc</c>.</summary>
    public long Qpc { get; }

    /// <summary>Typhon thread slot the sample was taken on, or <c>-1</c> for a non-Typhon thread.</summary>
    public int ThreadSlot { get; }

    /// <summary>0 = Managed (on-CPU proxy — cooperative GC mode), 1 = External (off-CPU — preemptive, blocked in native / syscall / I/O).</summary>
    public byte SampleType { get; }

    /// <summary>0-based index into the section's interned stack table.</summary>
    public uint StackIndex { get; }

    public CpuSampleRecord(long qpc, int threadSlot, byte sampleType, uint stackIndex)
    {
        Qpc = qpc;
        ThreadSlot = threadSlot;
        SampleType = sampleType;
        StackIndex = stackIndex;
    }
}

/// <summary>
/// One resolved frame symbol in the trailing <c>CpuSampleSection</c> of a <c>.typhon-trace</c> file (#351). Interned stacks reference frame symbols by
/// <see cref="FrameId"/>; a frame symbol carries the display method name and, when the frame resolved to source, a <see cref="FileId"/> / <see cref="Line"/>.
/// </summary>
/// <remarks>
/// <see cref="FileId"/> indexes the same <c>FileTable</c> the <c>SourceLocationManifest</c> uses — path interning is shared across both sections. A frame
/// with no resolved source (BCL / native / dynamic) has <see cref="Line"/> = 0 — <see cref="HasSource"/> reports it. (The <c>FileTable</c> does not reserve
/// a 0-index sentinel — index 0 is a real file — so <see cref="Line"/>, not <see cref="FileId"/>, is the "has source" discriminator.) Such a frame still
/// renders, it just has no editor link.
/// </remarks>
public readonly struct CpuFrameSymbol
{
    /// <summary>Section-local frame id (a dense <c>u16</c> space, distinct from the source-location manifest's site-id space).</summary>
    public ushort FrameId { get; }

    /// <summary>Index into the shared <c>FileTable</c>. Meaningful only when <see cref="HasSource"/> is true.</summary>
    public ushort FileId { get; }

    /// <summary>1-based source line, or 0 when the frame has no resolved source.</summary>
    public uint Line { get; }

    /// <summary>Display name of the frame's method; never null.</summary>
    public string Method { get; }

    /// <summary>True when the frame resolved to a source location (<see cref="FileId"/> / <see cref="Line"/> are usable).</summary>
    public bool HasSource => Line != 0;

    public CpuFrameSymbol(ushort frameId, ushort fileId, uint line, string method)
    {
        FrameId = frameId;
        FileId = fileId;
        Line = line;
        Method = method ?? string.Empty;
    }
}

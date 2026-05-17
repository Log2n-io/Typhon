using System.IO;
using Typhon.Profiler;
using Typhon.Workbench.Dtos.Profiler;
using Typhon.Workbench.Services;

namespace Typhon.Workbench.Sessions;

/// <summary>
/// The CPU-sample trailer section of a <c>.typhon-trace</c> (#351 Phase 4), loaded once per trace session: the raw sample
/// records + interned stack table, the resolved frame-symbol manifest, a flat <c>frameId → categoryId</c> table for fast
/// leaf attribution during the call-tree fold, and a per-thread-slot index over the (already qpc-sorted) sample array.
/// </summary>
/// <remarks>
/// Loaded straight from the source <c>.typhon-trace</c> — the same path <see cref="TraceSessionRuntime"/> uses for the #302
/// source-location manifest. Best-effort: a trace without a CPU-sample section, or any read failure, yields <see cref="Empty"/>.
/// </remarks>
public sealed class CpuSampleData
{
    /// <summary>Sentinel for traces with no CPU-sample section.</summary>
    public static readonly CpuSampleData Empty = new(
        [], [], CpuFrameManifestDto.Empty, [], []);

    /// <summary>Raw sample records, sorted by qpc and grouped per thread slot.</summary>
    public CpuSampleRecord[] Samples { get; }

    /// <summary>The interned stack table — each entry a leaf-first array of frame ids.</summary>
    public ushort[][] Stacks { get; }

    /// <summary>Resolved frame-symbol manifest + category table, served by the <c>cpu-frames</c> endpoint.</summary>
    public CpuFrameManifestDto Manifest { get; }

    /// <summary><c>frameId → categoryId</c>, indexed by frame id. Used for leaf-frame category attribution during the fold.</summary>
    public int[] CategoryByFrameId { get; }

    /// <summary>The per-thread-slot contiguous runs in <see cref="Samples"/> (§8.3 per-thread index) — each a qpc-sorted slice for windowed binary search.</summary>
    public (int Start, int Count)[] ThreadRuns { get; }

    /// <summary>True when the trace carries at least one CPU sample.</summary>
    public bool HasSamples => Samples.Length > 0;

    private CpuSampleData(
        CpuSampleRecord[] samples,
        ushort[][] stacks,
        CpuFrameManifestDto manifest,
        int[] categoryByFrameId,
        (int Start, int Count)[] threadRuns)
    {
        Samples = samples;
        Stacks = stacks;
        Manifest = manifest;
        CategoryByFrameId = categoryByFrameId;
        ThreadRuns = threadRuns;
    }

    /// <summary>
    /// Reads the CPU-sample trailer section from <paramref name="tracePath"/>, resolves frame symbols against the trace's
    /// FileTable, and projects everything into a ready-to-serve <see cref="CpuSampleData"/>. Returns <see cref="Empty"/> for
    /// traces without the section and on any read failure — absent CPU data is surfaced, never fatal to the session.
    /// </summary>
    public static CpuSampleData Load(string tracePath)
    {
        try
        {
            using var fs = File.OpenRead(tracePath);
            using var reader = new TraceFileReader(fs);
            reader.ReadHeader();
            if (!reader.TryReadCpuSampleSection(out var samples, out var stacks, out var frameSymbols) || samples.Length == 0)
            {
                return Empty;
            }
            reader.TryReadFileTable(out var files);

            var resolver = new CpuCategoryResolver();
            var maxFrameId = -1;
            for (var i = 0; i < frameSymbols.Length; i++)
            {
                if (frameSymbols[i].FrameId > maxFrameId)
                {
                    maxFrameId = frameSymbols[i].FrameId;
                }
            }

            var categoryByFrameId = new int[maxFrameId + 1];
            var frameDtos = new CpuFrameSymbolDto[frameSymbols.Length];
            for (var i = 0; i < frameSymbols.Length; i++)
            {
                var f = frameSymbols[i];
                var path = f.HasSource && f.FileId < files.Length ? files[f.FileId] ?? string.Empty : string.Empty;
                var categoryId = resolver.Resolve(path, f.Method);
                categoryByFrameId[f.FrameId] = categoryId;
                frameDtos[i] = new CpuFrameSymbolDto(f.FrameId, f.Method, path, (int)f.Line, categoryId);
            }

            var categories = resolver.Categories;
            var categoryDtos = new CpuCategoryDto[categories.Count];
            for (var i = 0; i < categories.Count; i++)
            {
                categoryDtos[i] = new CpuCategoryDto(i, categories[i]);
            }

            var manifest = new CpuFrameManifestDto(frameDtos, categoryDtos);
            var threadRuns = CpuSampleScope.BuildThreadRuns(samples);
            return new CpuSampleData(samples, stacks, manifest, categoryByFrameId, threadRuns);
        }
        catch
        {
            return Empty;
        }
    }
}

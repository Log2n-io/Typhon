using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// <see cref="IProfilerExporter"/> that writes the typed-event profiler's record stream to a <c>.typhon-trace</c> v3 binary file.
/// </summary>
/// <remarks>
/// <para>
/// <b>Format:</b> header + system/archetype/component-type tables + repeated LZ4-compressed record blocks. <see cref="TraceFileWriter"/> handles
/// block framing; this exporter owns the file stream and forwards <see cref="TraceRecordBatch.Payload"/> byte slices to it.
/// </para>
/// <para>
/// <b>Resource tree:</b> derives from <see cref="ResourceNode"/> so the exporter shows up under <c>Profiler/FileExporter</c>. Dispose is idempotent.
/// </para>
/// <para>
/// <b>Threading:</b> <see cref="ProcessBatch"/> is called from the dedicated exporter thread. Single writer.
/// </para>
/// </remarks>
internal sealed class FileExporter : ResourceNode, IProfilerExporter
{
    private readonly string _filePath;
    private FileStream _stream;
    private TraceFileWriter _writer;
    private TraceFileHeader _header;  // stashed at Initialize so Dispose can patch the trailer offsets in
    private bool _disposed;
    private long _batchesProcessed;
    private long _recordsProcessed;

    /// <summary>CPU samples handed in by <see cref="ProfilerLauncher"/> after the EventPipe sampler is stopped + parsed; written as a trailer at close (#351).</summary>
    private ParsedCpuSamples _cpuSamples;

    /// <summary>Diagnostic: how many batches this exporter has written so far.</summary>
    public long BatchesProcessed => _batchesProcessed;

    /// <summary>Diagnostic: total records written (sum of each batch's Count).</summary>
    public long RecordsProcessed => _recordsProcessed;

    /// <summary>
    /// Stash the parsed CPU-sample batch (#351). Called by <see cref="ProfilerLauncher.StopCpuSampler"/> after the EventPipe <c>.nettrace</c> is parsed,
    /// before the profiler session stops — the close path encodes it into the trace's CPU-sample trailer section.
    /// </summary>
    internal void SetCpuSamples(ParsedCpuSamples samples) => _cpuSamples = samples;

    public FileExporter(string filePath, IResource parent) : base("FileExporter", ResourceType.Service, parent ?? throw new ArgumentNullException(nameof(parent)))
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        Queue = new ExporterQueue(64);
    }

    /// <inheritdoc />
    public ExporterQueue Queue { get; }

    /// <summary>
    /// The exporter's lifecycle is owned by <see cref="TyphonProfiler"/> (attach → <c>Stop</c> drains then disposes it), not by the engine resource tree it
    /// is parented under for display. Returning <c>false</c> keeps a host's engine teardown from closing the trace file before the profiler's final drain.
    /// </summary>
    public override bool DisposeWithParent => false;

    /// <inheritdoc />
    public void Initialize(ProfilerSessionMetadata metadata)
    {
        if (metadata == null)
        {
            throw new ArgumentNullException(nameof(metadata));
        }

        // Open with FileAccess.ReadWrite so Dispose can rewind and patch the header with the trailer
        // (FileTable + SourceLocationManifest) offsets — see WriteSourceLocationManifestAtClose. ReadAccess
        // is needed for the seek-back; without it the rewrite throws "Stream does not support reading".
        _stream = new FileStream(_filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 64 * 1024);
        _writer = new TraceFileWriter(_stream);

        _header = new TraceFileHeader
        {
            Magic = TraceFileHeader.MagicValue,
            Version = TraceFileHeader.CurrentVersion,
            Flags = 0,
            TimestampFrequency = metadata.StopwatchFrequency,
            BaseTickRate = metadata.BaseTickRate,
            WorkerCount = (byte)metadata.WorkerCount,
            SystemCount = (ushort)metadata.Systems.Length,
            ArchetypeCount = (ushort)metadata.Archetypes.Length,
            ComponentTypeCount = (ushort)metadata.ComponentTypes.Length,
            CreatedUtcTicks = metadata.StartedUtc.Ticks,
            SamplingSessionStartQpc = metadata.SamplingSessionStartQpc,
            // FileTableOffset + SourceLocationManifestOffset are 0 — patched in WriteSourceLocationManifestAtClose.
        };
        _writer.WriteHeader(in _header);
        _writer.WriteSystemDefinitions(metadata.Systems);
        _writer.WriteArchetypes(metadata.Archetypes);
        _writer.WriteComponentTypes(metadata.ComponentTypes);
        _writer.WritePhases(metadata.Phases);

        // v7 static-structure tables. Order MUST match the reader (see TraceFileReader.Read* methods) — wire-positional, not section-table.
        // Empty inputs from hosts that don't introspect a live engine are valid: each writer emits a count prefix of 0 (or null
        // presence flag for RuntimeConfig) and downstream readers return empty lists / null.
        _writer.WriteComponentDefinitions(metadata.ComponentDefinitions);
        _writer.WriteArchetypeDefinitions(metadata.ArchetypeDefinitions);
        _writer.WriteIndexCatalog(metadata.IndexCatalog);
        _writer.WriteRuntimeConfig(metadata.RuntimeConfig);
        _writer.WriteEventQueueCatalog(metadata.EventQueues);
        _writer.WriteResourceGraphSnapshot(metadata.ResourceGraphNodes);
    }

    /// <summary>
    /// Append the trailing sections (SourceLocationManifest, CpuSampleSection, QuerySourceStringTable) and patch the header offsets in. Called from
    /// <see cref="Dispose(bool)"/> before the writer is closed. Each section is independently optional — if empty it is skipped and its header offset stays 0.
    /// The CPU-sample frame symbols and the source-location manifest share one <c>FileTable</c>, so the CPU encoder runs first (extending the file table) and
    /// the table is written once. A CPU-sample failure is contained — it never costs the source-location manifest.
    /// See claude/design/Profiler/10-profiler-source-attribution.md §4.6, /11-query-definition-export.md §4.8, /11-cpu-sampling-integration.md §6.5.
    /// </summary>
    private void WriteTrailerSectionsAtClose()
    {
        if (_writer == null)
        {
            return;
        }

        // ── Shared FileTable: source-location manifest files first, then (below) the CPU-sample frame file paths appended to the same table ──
        var (compileFiles, manifest) = RuntimeSourceLocationManifest.BuildMerged();
        var fileTable = new List<string>(compileFiles.Length + 16);
        var fileInterner = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < compileFiles.Length; i++)
        {
            var path = compileFiles[i] ?? string.Empty;
            fileTable.Add(path);
            fileInterner[path] = i; // BuildMerged yields unique paths; last-wins is harmless if that ever changes.
        }

        // ── CpuSampleSection (#351) — encode first so frame file paths land in the shared FileTable before it is written. Best-effort: a parse/encode
        //    failure must not cost the source-location manifest, so the encode is contained in its own try. ──
        CpuSampleSectionData cpuData = null;
        var cpuEncodeMs = 0L;
        if (_cpuSamples != null && _cpuSamples.SampleCount > 0)
        {
            try
            {
                var encodeSw = Stopwatch.StartNew();
                cpuData = CpuSampleSectionEncoder.Encode(_cpuSamples, fileTable, fileInterner);
                cpuEncodeMs = encodeSw.ElapsedMilliseconds;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[Typhon] FileExporter: CPU-sample encoding failed; the trace is written without a CPU-sample section. "
                    + ex.GetType().Name + ": " + ex.Message);
                cpuData = null;
            }
        }

        // ── SourceLocationManifest + the shared FileTable (compile-time call sites + runtime-resolved systems + CPU-sample frame paths) ──
        var hasFileContent = fileTable.Count > 0 || manifest.Length > 0;
        if (hasFileContent)
        {
            var (fileTableOffset, manifestOffset) = _writer.WriteSourceLocationManifest(fileTable, manifest);
            _header.FileTableOffset = fileTableOffset;
            _header.SourceLocationManifestOffset = manifestOffset;
        }

        if (cpuData != null)
        {
            var writeSw = Stopwatch.StartNew();
            var cpuOffset = _writer.WriteCpuSampleSection(cpuData.Samples, cpuData.Stacks, cpuData.FrameSymbols);
            _header.CpuSampleSectionOffset = cpuOffset;
            Console.WriteLine(
                $"[Typhon] FileExporter: CPU-sample trailer written — {cpuData.Samples.Count} records, "
                + $"{cpuData.Stacks.Count} interned stacks, {cpuData.FrameSymbols.Count} frame symbols "
                + $"(encode {cpuEncodeMs} ms, write {writeSw.ElapsedMilliseconds} ms)");
        }

        // ── QuerySourceStringTable (v9, #342) — deduped query definition/execution source strings ──
        var queryStrings = QuerySourceStringInterner.SnapshotStrings();
        if (queryStrings.Length > 1)  // > 1 because index 0 is the always-present sentinel
        {
            var offset = _writer.WriteQuerySourceStringTable(queryStrings);
            _header.QuerySourceStringTableOffset = offset;
        }

        // Only rewrite the header if at least one trailer section landed.
        if (hasFileContent || cpuData != null || queryStrings.Length > 1)
        {
            _writer.RewriteHeader(in _header);
            _writer.Flush();
        }
    }

    /// <inheritdoc />
    public void ProcessBatch(TraceRecordBatch batch)
    {
        if (_writer == null || batch.PayloadBytes == 0)
        {
            return;
        }

        _writer.WriteRecords(batch.Payload.AsSpan(0, batch.PayloadBytes), batch.Count);
        Interlocked.Increment(ref _batchesProcessed);
        Interlocked.Add(ref _recordsProcessed, batch.Count);
    }

    /// <inheritdoc />
    public void Flush() => _writer?.Flush();

    /// <inheritdoc />
    void IDisposable.Dispose() => Dispose(true);

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (disposing)
        {
            // Append the trailer sections BEFORE disposing the writer (which closes the stream).
            // Failures here are non-fatal — the trace stays valid without source attribution / CPU samples.
            try { WriteTrailerSectionsAtClose(); }
            catch
            {
                // ignored — trace remains usable, just without trailer sections.
            }

            try { _writer?.Dispose(); }
            catch
            {
                // ignored
            }

            _writer = null;
            _stream = null;
            try { Queue?.Dispose(); }
            catch
            {
                // ignored
            }
        }
        base.Dispose(disposing);
    }
}

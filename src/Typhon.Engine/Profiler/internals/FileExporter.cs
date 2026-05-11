using System;
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

    /// <summary>Diagnostic: how many batches this exporter has written so far.</summary>
    public long BatchesProcessed => _batchesProcessed;

    /// <summary>Diagnostic: total records written (sum of each batch's Count).</summary>
    public long RecordsProcessed => _recordsProcessed;

    public FileExporter(string filePath, IResource parent) : base("FileExporter", ResourceType.Service, parent ?? throw new ArgumentNullException(nameof(parent)))
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        Queue = new ExporterQueue(64);
    }

    /// <inheritdoc />
    public ExporterQueue Queue { get; }

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
    /// Append the source-location manifest as a trailer section and patch the header offsets in.
    /// Called from <see cref="Dispose(bool)"/> before the writer is closed. No-op if the generated <c>SourceLocations</c> table is empty (zero attributed
    /// sites). See claude/design/Profiler/10-profiler-source-attribution.md §4.6.
    /// </summary>
    private void WriteSourceLocationManifestAtClose()
    {
        if (_writer == null)
        {
            return;
        }
        // Merged: compile-time call sites + runtime-resolved system entries (DagScheduler populates
        // the runtime side at construction). Empty manifests skip the trailer write entirely.
        var (files, manifest) = RuntimeSourceLocationManifest.BuildMerged();
        if (files.Length == 0 || manifest.Length == 0)
        {
            return;
        }

        var (fileTableOffset, manifestOffset) = _writer.WriteSourceLocationManifest(files, manifest);
        _header.FileTableOffset = fileTableOffset;
        _header.SourceLocationManifestOffset = manifestOffset;
        _writer.RewriteHeader(in _header);
        _writer.Flush();
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
            // Append the source-location manifest BEFORE disposing the writer (which closes the stream).
            // Failures here are non-fatal — the trace stays valid without source attribution.
            try { WriteSourceLocationManifestAtClose(); }
            catch
            {
                // ignored — trace remains usable, just without source attribution.
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

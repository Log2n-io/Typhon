using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Diagnostics.NETCore.Client;

namespace Typhon.Engine.Internals;

/// <summary>
/// In-process CPU stack-sampling capture. Opens an EventPipe session against the current process via <see cref="DiagnosticsClient"/> — the library
/// <c>dotnet-trace</c> is itself built on, so no global tool and no special CLR launch are needed — and streams the raw EventPipe data to a
/// <c>.nettrace</c> companion file next to the <c>.typhon-trace</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope (Phase 1, #351).</b> This type only <i>captures</i>: it produces the <c>.nettrace</c> companion and the <see cref="SamplingSessionStartQpc"/>
/// anchor. Parsing that stream, resolving symbols, and embedding CPU samples into the <c>.typhon-trace</c> are later phases — the companion file is left
/// in place on <see cref="Stop"/> as the parser's input.
/// </para>
/// <para>
/// <b>Why buffer to a file.</b> CPU-sample stacks are unresolved until rundown events are emitted at session stop; an in-process consumer that parses the
/// stream live can deadlock against rundown (dotnet/runtime #45518). Copying the raw stream to a file and parsing the finished buffer afterward avoids that.
/// </para>
/// <para>
/// <b>Best-effort.</b> If the runtime diagnostics server is unavailable or the session cannot start, <see cref="Start"/> logs one line and returns — the
/// profiling session still produces its <c>.typhon-trace</c>, just without a CPU-sample companion. It never throws into the host.
/// </para>
/// </remarks>
internal sealed class CpuSamplerSession : IDisposable
{
    /// <summary>The .NET runtime's CPU stack-sampling EventPipe provider — the runtime runs a ~1 kHz sampler thread while it is enabled.</summary>
    private const string SampleProfilerProvider = "Microsoft-DotNETCore-SampleProfiler";

    /// <summary>The CLR runtime provider — enabled alongside the sampler so rundown (Loader/Jit/Method) data is present for later symbol resolution.</summary>
    private const string DotNetRuntimeProvider = "Microsoft-Windows-DotNETRuntime";

    /// <summary>Keyword mask for <see cref="DotNetRuntimeProvider"/> — the standard cpu-sampling set (Loader + Jit + NGen + rundown), matching dotnet-trace.</summary>
    private const long DotNetRuntimeKeywords = 0x4C14FCCBD;

    /// <summary>How long <see cref="Stop"/> waits for the background copy to drain the stream tail (which carries rundown) before giving up.</summary>
    private static readonly TimeSpan StopDrainTimeout = TimeSpan.FromSeconds(30);

    private EventPipeSession _session;
    private FileStream _output;
    private Task _copyTask;
    private string _netTracePath;
    private volatile bool _started;
    private bool _disposed;

    /// <summary>
    /// <see cref="Stopwatch.GetTimestamp"/> (QPC on Windows) captured at session start, or <c>0</c> when no session is running. Written into the trace
    /// header so the viewer can correlate relative EventPipe sample timestamps against the absolute trace timeline.
    /// </summary>
    public long SamplingSessionStartQpc { get; private set; }

    /// <summary>Whether a capture session is currently open.</summary>
    public bool IsRunning => _started;

    /// <summary>
    /// Path of the <c>.nettrace</c> companion this session writes (derived from the trace path passed to <see cref="Start"/>). Set by <see cref="Start"/>
    /// and retained after <see cref="Stop"/> so the capture can be parsed (#351 Phase 2/3). <c>null</c> before the first <see cref="Start"/>.
    /// </summary>
    public string NetTracePath => _netTracePath;

    /// <summary>
    /// Open an in-process EventPipe CPU-sampling session and begin streaming it to a <c>.nettrace</c> companion next to <paramref name="traceFilePath"/>.
    /// Idempotent — a second call while running is a no-op. Best-effort: a failure to start is logged and swallowed (see the type remarks).
    /// </summary>
    public void Start(string traceFilePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(traceFilePath);
        if (_started)
        {
            return;
        }

        _netTracePath = Path.ChangeExtension(traceFilePath, ".nettrace");

        try
        {
            var providers = new[]
            {
                new EventPipeProvider(SampleProfilerProvider, EventLevel.Informational),
                new EventPipeProvider(DotNetRuntimeProvider, EventLevel.Informational, DotNetRuntimeKeywords),
            };

            // QPC anchor — captured immediately before the session opens so it brackets the first sample the runtime can take.
            SamplingSessionStartQpc = Stopwatch.GetTimestamp();

            var client = new DiagnosticsClient(Environment.ProcessId);
            _session = client.StartEventPipeSession(providers, requestRundown: true);

            _output = new FileStream(_netTracePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            _copyTask = _session.EventStream.CopyToAsync(_output);
            _started = true;
        }
        catch (Exception ex)
        {
            // Diagnostics server disabled, IPC failure, unwritable path, … — degrade: the profiling session still produces its .typhon-trace.
            Console.Error.WriteLine(
                "[Typhon] CpuSamplerSession: could not start in-process CPU sampling; continuing without it. "
                + ex.GetType().Name + ": " + ex.Message);
            SamplingSessionStartQpc = 0;
            CleanupAfterFailure();
        }
    }

    /// <summary>
    /// Stop the capture session and finish writing the <c>.nettrace</c> companion. Idempotent. The companion file is left on disk — it is the input for
    /// the later parsing phase.
    /// </summary>
    public void Stop()
    {
        if (!_started)
        {
            return;
        }
        _started = false;

        try
        {
            // Stop() ends collection and flushes rundown into the stream tail; the background copy then sees EOF and completes.
            // The two sub-steps are timed separately — rundown flush vs. stream drain are the usual epilogue-cost suspects.
            var sw = Stopwatch.StartNew();
            _session.Stop();
            var stopMs = sw.ElapsedMilliseconds;
            var drained = _copyTask == null || _copyTask.Wait(StopDrainTimeout);
            var drainMs = sw.ElapsedMilliseconds - stopMs;
            if (!drained)
            {
                Console.Error.WriteLine("[Typhon] CpuSamplerSession: timed out draining the EventPipe stream; the .nettrace companion may be truncated.");
            }
            _output?.Flush();
            var sizeKb = 0L;
            try
            {
                sizeKb = new FileInfo(_netTracePath).Length / 1024;
            }
            catch
            {
                // Size is a diagnostic nicety only — a stat failure must not derail the stop path.
            }
            Console.WriteLine(
                $"[Typhon] CpuSamplerSession: CPU-sample capture written to {_netTracePath} ({sizeKb} KB) — "
                + $"EventPipe stop {stopMs} ms, stream drain {drainMs} ms");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[Typhon] CpuSamplerSession: error stopping CPU sampling. " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            SafeDispose(_output);
            SafeDispose(_session);
            _output = null;
            _session = null;
            _copyTask = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Stop();
    }

    /// <summary>Tear down half-opened state after a failed <see cref="Start"/> so a later call doesn't observe a partially-initialised session.</summary>
    private void CleanupAfterFailure()
    {
        SafeDispose(_session);
        SafeDispose(_output);
        _session = null;
        _output = null;
        _copyTask = null;
        _started = false;
    }

    /// <summary>Dispose a resource, swallowing any error — used on the failure / teardown paths where a dispose exception is not actionable.</summary>
    private static void SafeDispose(IDisposable resource)
    {
        if (resource == null)
        {
            return;
        }
        try
        {
            resource.Dispose();
        }
        catch
        {
            // Best-effort cleanup — a dispose error during teardown is not actionable.
        }
    }
}

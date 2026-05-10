using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Manages CPU sampling by launching <c>dotnet-trace</c> as an external process.
/// Produces a <c>.nettrace</c> file alongside the <c>.typhon-trace</c> file with full symbol resolution (method names, module names).
/// </summary>
/// <remarks>
/// <para>
/// Why external process: self-connected EventPipe sessions have a known limitation where rundown events (needed for mapping instruction pointers to method names)
/// are not properly captured. The <c>dotnet-trace</c> tool handles this correctly because it connects from an external process via the diagnostic IPC port.
/// </para>
/// <para>
/// Overhead: The runtime's <c>Microsoft-DotNETCore-SampleProfiler</c> provider suspends the execution engine ~1000 times per second. Each suspension
/// takes ~3-5 µs, adding ~0.3-0.5% overhead. The external <c>dotnet-trace</c> process consumes minimal additional resources (just draining the IPC pipe to disk).
/// </para>
/// </remarks>
internal sealed class EventPipeSampler : IDisposable
{
    private Process _traceProcess;
    private bool _disposed;

    /// <summary>Path to the <c>.nettrace</c> file being written.</summary>
    public string NetTracePath { get; private set; }

    /// <summary>
    /// QPC timestamp captured just before <c>dotnet-trace</c> attaches.
    /// Used to correlate EventPipe relative timestamps with trace file absolute timestamps.
    /// </summary>
    public long SessionStartQpc { get; private set; }

    /// <summary>
    /// Starts CPU sampling by launching <c>dotnet-trace collect</c> targeting the current process.
    /// </summary>
    /// <param name="traceFilePath">Path to the main <c>.typhon-trace</c> file. The <c>.nettrace</c> is placed alongside it.</param>
    /// <param name="durationSeconds">
    /// How long to collect samples. <c>dotnet-trace</c> stops itself after this duration, which ensures proper session termination including rundown events
    /// (needed for method name resolution).
    /// Default: 15 seconds.
    /// </param>
    public void Start(string traceFilePath, int durationSeconds = 15)
    {
        NetTracePath = Path.ChangeExtension(traceFilePath, ".nettrace");
        SessionStartQpc = Stopwatch.GetTimestamp();

        var pid = Process.GetCurrentProcess().Id;
        var duration = TimeSpan.FromSeconds(durationSeconds);

        // Launch dotnet-trace as external process with fixed duration.
        // Using --duration ensures clean session termination with full rundown (method name resolution requires the rundown events at session end).
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet-trace",
            ArgumentList =
            {
                "collect",
                "-p", pid.ToString(),
                "--profile", "dotnet-sampled-thread-time",
                "--duration", duration.ToString(@"hh\:mm\:ss"),
                "-o", NetTracePath,
                "--format", "NetTrace"
            },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        _traceProcess = Process.Start(psi);

        if (_traceProcess == null)
        {
            throw new InvalidOperationException("Failed to start dotnet-trace. Ensure dotnet-trace is installed: dotnet tool install -g dotnet-trace");
        }

        // Wait briefly for dotnet-trace to attach
        Thread.Sleep(500);
    }

    /// <summary>
    /// Stops the <c>dotnet-trace</c> process gracefully by closing its stdin (which signals it to stop collection).
    /// </summary>
    public void Stop()
    {
        if (_traceProcess == null || _traceProcess.HasExited)
        {
            return;
        }

        try
        {
            // Wait for dotnet-trace to finish naturally (it will stop at the configured duration).
            // The rundown events that map instruction pointers to method names are emitted at session end.
            if (!_traceProcess.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                _traceProcess.Kill();
            }
        }
        catch
        {
            try { _traceProcess.Kill(); } catch { }
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
        _traceProcess?.Dispose();
    }
}

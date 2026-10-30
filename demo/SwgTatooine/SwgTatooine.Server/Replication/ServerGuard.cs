using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace SwgTatooine.Server;

/// <summary>
/// What a served world does when the engine under it is in trouble (#1532): sheds load while storage is Overloaded or Starved, fails fast while a circuit
/// breaker is open, bounds the database work requests may run at once, and stops the process — with a deadline — when the tick aborts or stalls.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> Two reproductions (2026-10-28 and 2026-10-29) showed a server that did not react to anything. After a tick abort it stopped ticking but kept
/// serving HTTP and accepting logins for 50 minutes, burning 20+ cores. On a cold start, ~230 inventory and ledger readers ran at once on thread-pool threads;
/// every failed request was answered with an exception and replaced at once by a bot's retry, so nothing ever drained, and a clean stop was not honoured
/// either. The engine has its own defects to fix there (#1532); this is the server expecting them.
/// </para>
/// <para>
/// <b>Four decisions, one place.</b>
/// </para>
/// <list type="bullet">
///   <item><b>The gate</b>: every request on a <see cref="IsDatabasePath">database path</see> waits for one of <see cref="Width"/> places, ASYNCHRONOUSLY —
///   holding no thread and no epoch — and is answered 503 with <c>Retry-After</c> after <see cref="MaxWait"/>. Logins are not gated here: <c>/auth</c>
///   has its own gate, sized for its KDF, and nesting the two would let queued logins hold places reads need.</item>
///   <item><b>Shedding</b>: while the engine reports Overloaded or Starved storage, or while the breaker is open, new logins, new WebSocket sessions and
///   database reads are answered 503. Sessions already open are left alone: what they cost is the tick's, which the engine already slows.</item>
///   <item><b>The breaker</b>: <see cref="TripCount"/> engine failures (a <c>Typhon.Engine</c> exception escaping a request) within
///   <see cref="TripWindow"/> open it for <see cref="CoolDown"/>; each trip is logged with what tripped it. A failing request is answered 503, not left
///   to the host's unhandled-exception page and its stack trace in the log.</item>
///   <item><b>Stopping</b>: a tick abort, or a tick that has not advanced for the wedge timeout, stops the process. Everything but the health and stats
///   routes is refused from that moment, the host is asked to stop, and a deadline is armed: past it, the process is killed. A clean stop asked from
///   outside (Ctrl+C, Ctrl+Break) arms the same deadline, because a stop that waits behind a wedged tick is no stop.</item>
/// </list>
/// </remarks>
public sealed class ServerGuard
{
    /// <summary>Exit code of a process the guard stopped for a fault: a tick abort or a stalled tick.</summary>
    public const int FaultExitCode = 4;

    /// <summary>Exit code of a process killed because it did not stop within its deadline.</summary>
    public const int DeadlineExitCode = 5;

    // The routes under /swg that answer from memory, never from the database: served whatever the engine's state, never gated.
    private static readonly string[] InMemoryRoutes =
    [
        "/swg/stats", "/swg/economy/stats", "/swg/crowd/stats", "/swg/facilities", "/swg/market", "/swg/market/prices",
    ];

    // What a stopping server still answers: whoever watches it must be able to see it stop.
    private static readonly string[] AlwaysServed = ["/healthz", "/typhon/stats.json"];

    private readonly SemaphoreSlim _gate;
    private readonly Func<long> _now;
    private readonly Queue<(long At, string Kind)> _failures = new();
    private readonly object _lock = new();
    private long _openUntil;
    private bool _wasOpen;
    private int _storage;
    private string _stopReason;
    private Action _stopHost;
    private long _shed;
    private long _refused;
    private long _failed;
    private long _trips;

    /// <param name="width">Database requests at once.</param>
    /// <param name="maxWait">How long a request waits for a place before it is answered 503.</param>
    /// <param name="tripCount">Engine failures that open the breaker…</param>
    /// <param name="tripWindow">…within this window.</param>
    /// <param name="coolDown">How long the breaker stays open.</param>
    /// <param name="now">The clock, in ms: <see cref="Environment.TickCount64"/> unless a check drives it.</param>
    public ServerGuard(int width, TimeSpan maxWait, int tripCount, TimeSpan tripWindow, TimeSpan coolDown, Func<long> now = null)
    {
        Width = Math.Max(1, width);
        MaxWait = maxWait;
        TripCount = Math.Max(1, tripCount);
        TripWindow = tripWindow;
        CoolDown = coolDown;
        _now = now ?? (static () => Environment.TickCount64);
        _gate = new SemaphoreSlim(Width, Width);
    }

    /// <summary>The guard a served world uses: a quarter of the cores (2 to 8), 2 s of waiting, 20 failures in 10 s open the breaker for 15 s.</summary>
    public static ServerGuard ForServer(TimeSpan stopDeadline, TimeSpan wedgeTimeout)
        => new(Math.Clamp(Environment.ProcessorCount / 4, 2, 8), TimeSpan.FromSeconds(2), 20, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15))
        {
            StopDeadline = stopDeadline,
            WedgeTimeout = wedgeTimeout,
        };

    /// <summary>How long a stopping process has before it is killed (<c>--stop-deadline-s</c>).</summary>
    public TimeSpan StopDeadline { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the tick may stand still before the server stops (<c>--wedge-s</c>); zero watches nothing.</summary>
    public TimeSpan WedgeTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public int Width { get; }
    public TimeSpan MaxWait { get; }
    public int TripCount { get; }
    public TimeSpan TripWindow { get; }
    public TimeSpan CoolDown { get; }

    /// <summary>Requests answered 503 because the server was shedding load.</summary>
    public long Shed => Interlocked.Read(ref _shed);

    /// <summary>Requests answered 503 because the gate stayed full for <see cref="MaxWait"/>.</summary>
    public long Refused => Interlocked.Read(ref _refused);

    /// <summary>Requests that failed with an engine exception.</summary>
    public long Failed => Interlocked.Read(ref _failed);

    /// <summary>Times the breaker opened.</summary>
    public long Trips => Interlocked.Read(ref _trips);

    /// <summary>Why the server is stopping, or <see langword="null"/> while it serves.</summary>
    public string StopReason => Volatile.Read(ref _stopReason);

    /// <summary>Whether the guard stopped the server for a fault.</summary>
    public bool Faulted => StopReason is { } reason && !reason.StartsWith("asked", StringComparison.Ordinal);

    /// <summary>The engine's storage health, as last reported.</summary>
    public Typhon.Engine.StorageHealthState Storage => (Typhon.Engine.StorageHealthState)Volatile.Read(ref _storage);

    /// <summary>Whether the breaker is open now. Logs its closing, once.</summary>
    public bool BreakerOpen
    {
        get
        {
            lock (_lock)
            {
                var open = _now() < _openUntil;
                if (_wasOpen && !open)
                {
                    _wasOpen = false;
                    Console.WriteLine("  guard: circuit breaker CLOSED after its cool-down");
                }

                return open;
            }
        }
    }

    /// <summary>Whether new logins, sessions and database reads are refused now.</summary>
    public bool Shedding => Volatile.Read(ref _storage) >= (int)Typhon.Engine.StorageHealthState.Overloaded || BreakerOpen;

    /// <summary>The engine's storage health changed: <c>DatabaseEngine.StorageHealthChanged</c>.</summary>
    public void OnStorageHealth(Typhon.Engine.StorageHealthState state)
    {
        var before = (Typhon.Engine.StorageHealthState)Interlocked.Exchange(ref _storage, (int)state);
        var shedding = state >= Typhon.Engine.StorageHealthState.Overloaded;
        if (shedding != before >= Typhon.Engine.StorageHealthState.Overloaded)
        {
            Console.WriteLine(shedding
                ? $"  guard: storage {state}: SHEDDING load — new logins, sessions and database reads are answered 503"
                : $"  guard: storage {state}: load accepted again");
        }
    }

    /// <summary>An engine failure escaped a request: counted toward the breaker, which opens at <see cref="TripCount"/> within <see cref="TripWindow"/>.</summary>
    public void RecordEngineFailure(Exception e)
    {
        Interlocked.Increment(ref _failed);
        lock (_lock)
        {
            var now = _now();
            _failures.Enqueue((now, e.GetType().Name));
            while (_failures.Count > 0 && now - _failures.Peek().At > (long)TripWindow.TotalMilliseconds)
            {
                _failures.Dequeue();
            }

            if (_failures.Count < TripCount || now < _openUntil)
            {
                return;
            }

            // What tripped it, by exception type: the breaker is only useful if the log says why it opened.
            var kinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var (_, kind) in _failures)
            {
                kinds[kind] = kinds.TryGetValue(kind, out var n) ? n + 1 : 1;
            }

            var list = new List<string>(kinds.Count);
            foreach (var (kind, n) in kinds)
            {
                list.Add($"{n} {kind}");
            }

            _openUntil = now + (long)CoolDown.TotalMilliseconds;
            _wasOpen = true;
            _failures.Clear();
            Interlocked.Increment(ref _trips);
            Console.WriteLine($"  !! guard: circuit breaker OPEN for {CoolDown.TotalSeconds:0} s — {string.Join(", ", list)} within "
                + $"{TripWindow.TotalSeconds:0} s; database requests are answered 503");
        }
    }

    /// <summary>Whether an exception is the engine's: a <c>Typhon.Engine</c> type, or a timeout.</summary>
    public static bool IsEngineFailure(Exception e)
        => e is TimeoutException || (e.GetType().Namespace?.StartsWith("Typhon.Engine", StringComparison.Ordinal) ?? false)
           || (e.InnerException != null && IsEngineFailure(e.InnerException));

    /// <summary>Whether a request may reach the database: <c>/swg/…</c> except the routes answering from memory.</summary>
    public static bool IsDatabasePath(PathString path)
    {
        if (!path.StartsWithSegments("/swg", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var route in InMemoryRoutes)
        {
            if (path.Equals(route, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The host to stop when the guard stops the server; also arms the deadline when the host stops for any other reason.</summary>
    public void Attach(Action stopHost) => _stopHost = stopHost;

    /// <summary>
    /// Stops the server: refuses everything but the health and stats routes from now on, asks the host to stop, and kills the process if it has not
    /// exited within <paramref name="deadline"/>. Idempotent: the first reason is the one kept.
    /// </summary>
    public void Stop(string reason) => Stop(reason, StopDeadline);

    /// <inheritdoc cref="Stop(string)"/>
    public void Stop(string reason, TimeSpan deadline)
    {
        if (Interlocked.CompareExchange(ref _stopReason, reason, null) != null)
        {
            return;
        }

        Console.WriteLine($"  !! guard: STOPPING — {reason}; the process is killed if it has not exited in {deadline.TotalSeconds:0} s");
        ArmDeadline(deadline);
        _stopHost?.Invoke();
    }

    /// <summary>
    /// Kills the process if it is still alive after <paramref name="deadline"/>. A dedicated thread, not the pool: a starved pool is one of the ways a stop
    /// hangs.
    /// </summary>
    private void ArmDeadline(TimeSpan deadline)
    {
        var thread = new Thread(() =>
        {
            Thread.Sleep(deadline);
            Console.WriteLine($"  !! guard: the process did not stop within {deadline.TotalSeconds:0} s ({StopReason}); killing it");
            Console.Out.Flush();

            // Exit with the code that says so; and if exiting itself hangs — it runs the process-exit handlers, which a wedged engine can block — kill the
            // process five seconds later, which cannot hang but cannot choose its code either.
            var kill = new Thread(() =>
            {
                Thread.Sleep(5000);
                Process.GetCurrentProcess().Kill();
            })
            {
                IsBackground = true,
                Name = "SwgStopKill",
            };
            kill.Start();
            Environment.Exit(DeadlineExitCode);
        })
        {
            IsBackground = true,
            Name = "SwgStopDeadline",
        };
        thread.Start();
    }

    /// <summary>
    /// Watches the tick: one that has not advanced for <paramref name="wedge"/> while the server is not stopping stops it. Started once the runtime
    /// ticks; on a dedicated thread for the same reason as the deadline.
    /// </summary>
    public void WatchTick(Func<long> tick) => WatchTick(tick, WedgeTimeout, StopDeadline);

    /// <inheritdoc cref="WatchTick(Func{long})"/>
    public void WatchTick(Func<long> tick, TimeSpan wedge, TimeSpan deadline)
    {
        if (wedge <= TimeSpan.Zero)
        {
            return;
        }

        var thread = new Thread(() =>
        {
            var last = tick();
            var since = _now();
            while (StopReason == null)
            {
                Thread.Sleep(1000);
                var now = tick();
                if (now != last)
                {
                    last = now;
                    since = _now();
                    continue;
                }

                if (_now() - since >= (long)wedge.TotalMilliseconds)
                {
                    Stop($"the tick has not advanced past {now} for {wedge.TotalSeconds:0} s (storage {Storage})", deadline);
                }
            }
        })
        {
            IsBackground = true,
            Name = "SwgTickWatchdog",
        };
        thread.Start();
    }

    /// <summary>The middleware: shedding, the gate and the breaker, on every request.</summary>
    public async Task Handle(HttpContext context, RequestDelegate next)
    {
        var path = context.Request.Path;
        if (StopReason != null)
        {
            if (Served(path))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            Refuse(context, 5);
            return;
        }

        var session = path.StartsWithSegments("/ws", StringComparison.OrdinalIgnoreCase);
        var login = path.StartsWithSegments("/auth", StringComparison.OrdinalIgnoreCase);
        var database = IsDatabasePath(path);
        if (!session && !login && !database)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (Shedding)
        {
            Interlocked.Increment(ref _shed);
            Refuse(context, 2);
            return;
        }

        // A session is long-lived and a login has its own gate: admitted, not gated.
        if (!database)
        {
            await Guarded(context, next).ConfigureAwait(false);
            return;
        }

        if (!await _gate.WaitAsync(MaxWait, context.RequestAborted).ConfigureAwait(false))
        {
            Interlocked.Increment(ref _refused);
            Refuse(context, 1);
            return;
        }

        try
        {
            await Guarded(context, next).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // The request, with an engine failure counted toward the breaker and answered 503 rather than left to escape.
    private async Task Guarded(HttpContext context, RequestDelegate next)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception e) when (IsEngineFailure(e) && !context.Response.HasStarted)
        {
            RecordEngineFailure(e);
            Refuse(context, 2);
        }
    }

    private static bool Served(PathString path)
    {
        foreach (var route in AlwaysServed)
        {
            if (path.Equals(route, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void Refuse(HttpContext context, int retryAfterS)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = retryAfterS.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}

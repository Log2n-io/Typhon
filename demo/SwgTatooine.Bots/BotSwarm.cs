using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Client;
using Typhon.Protocol;

namespace SwgTatooine.Bots;

/// <summary>What a swarm needs to know.</summary>
public sealed class BotSwarmOptions
{
    /// <summary>Where the server is: <c>ws://…</c> or <c>tcp://host:port</c>.</summary>
    public Uri Endpoint { get; init; }

    /// <summary>How many sessions to open.</summary>
    public int Count { get; init; } = 100;

    /// <summary>The session kind the server's admission expects.</summary>
    public string Kind { get; init; } = "god";

    /// <summary>How often the shared timer fires, in hertz. Pings and region updates are both derived from it.</summary>
    public int TickHz { get; init; } = 4;

    /// <summary>How far a camera's footprint reaches, in metres.</summary>
    public double RegionRadiusM { get; init; } = 256;

    /// <summary>How far a camera travels per second along its orbit, in metres.</summary>
    public double CameraSpeedMps { get; init; } = 12;

    /// <summary>The byte budget each camera declares, in KiB per second.</summary>
    public int BudgetKiBps { get; init; } = 256;

    /// <summary>How many ticks between region updates. The command is rate-limited to 5 Hz on the wire, so this must not outrun it.</summary>
    public int RegionEveryTicks { get; init; } = 4;

    /// <summary>How long to spread the connects over, so a hundred handshakes do not arrive in one tick.</summary>
    public TimeSpan ConnectStagger { get; init; } = TimeSpan.FromMilliseconds(20);
}

/// <summary>
/// A population of scripted god cameras over <see cref="TyphonClient"/>, driven by one timer.
/// </summary>
/// <remarks>
/// <para>
/// <b>One timer, not one per bot.</b> The load generator's job is to put a hundred — eventually a thousand — sessions on a server and measure what the server
/// does, which only works if the generator is not itself the expensive thing. A client per timer is a timer registration and a wakeup per client per period;
/// one timer walking a list is one of each. That is why <see cref="ClientOptions.PingHz"/> can be zero: the swarm drives the ping cadence the server's lag
/// skip depends on, from the same tick that moves the cameras.
/// </para>
/// <para>
/// <b>A disconnect is the measurement, not an inconvenience.</b> The smoke criterion is zero UNEXPECTED disconnects, so the swarm counts them by close code
/// rather than merely surviving them: 1013 means the server shed this client under load, 4001 means the swarm failed to ping, and either is a finding about
/// the run rather than noise to retry through. Reconnect is therefore off by default — a load generator that silently reconnects reports a healthy run over
/// a server that dropped half its sessions.
/// </para>
/// <para>
/// <b>The cameras move because a still one measures nothing.</b> A stationary region yields the same interest set every tick, so the server's per-session
/// work collapses to what a single cached frame costs and the numbers flatter it. Each bot orbits its own centre at its own phase, which keeps the union of
/// interest sets broad and the per-session sets genuinely different.
/// </para>
/// </remarks>
public sealed class BotSwarm : IAsyncDisposable
{
    private readonly BotSwarmOptions _options;
    private readonly List<Bot> _bots = [];
    private readonly CancellationTokenSource _stopping = new();
    private Task _driver;

    /// <summary>Builds a swarm. Nothing connects until <see cref="StartAsync"/>.</summary>
    /// <param name="options">The swarm's configuration.</param>
    public BotSwarm(BotSwarmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Endpoint, nameof(options.Endpoint));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Count, nameof(options.Count));

        _options = options;
    }

    /// <summary>How many sessions are open right now.</summary>
    public int Connected
    {
        get
        {
            var live = 0;
            foreach (var bot in _bots)
            {
                if (bot.Client.IsConnected)
                {
                    live++;
                }
            }

            return live;
        }
    }

    /// <summary>How many sessions were closed by the server or the transport, by close code.</summary>
    /// <remarks>A snapshot: the live dictionary is written by every client's receive loop, so handing it out would hand out a race.</remarks>
    public IReadOnlyDictionary<ushort, int> Disconnects
    {
        get
        {
            lock (_reportLock)
            {
                return new Dictionary<ushort, int>(_disconnects);
            }
        }
    }

    /// <summary>Messages received across every session.</summary>
    public long MessagesReceived
    {
        get
        {
            var total = 0L;
            foreach (var bot in _bots)
            {
                total += bot.Client.MessagesReceived;
            }

            return total;
        }
    }

    /// <summary>Faults raised by any client's receive loop — a frame that could not be applied.</summary>
    public int Faults => Volatile.Read(ref _faults);

    /// <summary>
    /// A built-in server metric as the server last reported it, or <c>NaN</c> when nothing has carried one yet.
    /// </summary>
    /// <remarks>
    /// The server's own view of the run, read the way any client reads it: out of the <c>STATS</c> block, through the catalog. That matters for a load
    /// generator — a number scraped from the server process measures the server under a harness, whereas this one measures what the server told its
    /// clients, which is the claim AC-1 actually makes.
    /// </remarks>
    /// <param name="name">The metric's catalog name, e.g. <c>typhon.subscriptions.track.p99</c>.</param>
    public double ServerMetric(string name)
    {
        // From the session holding the MOST blocks, which is the one whose reading is freshest. Taking the max of the VALUE across the population was the
        // mistake this replaced: a maximum never falls, so a stale peak outlives every later reading and the number looks frozen or healthy for reasons that
        // have nothing to do with the server.
        Bot freshest = null;
        var best = -1L;
        foreach (var bot in _bots)
        {
            var blocks = bot.Client.Store?.StatsBlocks ?? 0;
            if (blocks > best)
            {
                best = blocks;
                freshest = bot;
            }
        }

        var plan = freshest?.Client.Plan;
        var store = freshest?.Client.Store;
        if (plan == null || store == null)
        {
            return double.NaN;
        }

        foreach (var metric in plan.ServerMetrics)
        {
            if (metric.Name == name)
            {
                return store.ServerMetricValues[metric.Offset][0];
            }
        }

        return double.NaN;
    }

    /// <summary>
    /// The per-system mean durations the server publishes as <c>typhon.system.mean</c>, largest first, read from the freshest session.
    /// </summary>
    /// <remarks>
    /// This is the server's own scheduler breakdown, arriving over the wire at no cost to it — the labels name the systems, one value each. It answers
    /// "where is the tick going" without attaching a profiler, which makes it the right first instrument and a profiler the second.
    /// </remarks>
    /// <param name="name">The labelled metric to read.</param>
    /// <param name="top">How many rows to return.</param>
    public IReadOnlyList<(string Name, double Ms)> LabelledMetric(string name, int top)
    {
        Bot freshest = null;
        var best = -1L;
        foreach (var bot in _bots)
        {
            var blocks = bot.Client.Store?.StatsBlocks ?? 0;
            if (blocks > best)
            {
                best = blocks;
                freshest = bot;
            }
        }

        var plan = freshest?.Client.Plan;
        var store = freshest?.Client.Store;
        if (plan == null || store == null)
        {
            return [];
        }

        foreach (var metric in plan.ServerMetrics)
        {
            if (metric.Name != name)
            {
                continue;
            }

            var labels = metric.Metric.Labels;
            var values = store.ServerMetricValues[metric.Offset];
            var rows = new List<(string Name, double Ms)>(labels.Length);
            for (var i = 0; i < labels.Length && i < values.Length; i++)
            {
                rows.Add((labels[i], values[i]));
            }

            rows.Sort((a, b) => b.Ms.CompareTo(a.Ms));
            return rows.GetRange(0, Math.Min(top, rows.Count));
        }

        return [];
    }

    /// <summary>How many <c>TICK</c> frames each session has applied: the total, the smallest, the largest, and how many have applied none.</summary>
    /// <remarks>
    /// Distinguishes a session that is connected from one that is being SERVED. A socket that stays open while the server never produces a frame for it
    /// looks perfectly healthy in every connection-level number there is, which is why the population's frame counts are reported beside them.
    /// </remarks>
    public (long Total, long Min, long Max, int Unserved) Frames()
    {
        var total = 0L;
        var min = long.MaxValue;
        var max = 0L;
        var unserved = 0;

        foreach (var bot in _bots)
        {
            var frames = bot.Client.Store?.Frames ?? 0;
            total += frames;
            min = Math.Min(min, frames);
            max = Math.Max(max, frames);
            if (frames == 0)
            {
                unserved++;
            }
        }

        return (total, min == long.MaxValue ? 0 : min, max, unserved);
    }

    /// <summary>Entity records per frame, averaged over the population: how much of a watched world actually changes in a tick.</summary>
    public double RecordsPerFrame
    {
        get
        {
            var records = 0L;
            var frames = 0L;
            foreach (var bot in _bots)
            {
                records += bot.Client.Store?.Records ?? 0;
                frames += bot.Client.Store?.Frames ?? 0;
            }

            return frames == 0 ? 0 : (double)records / frames;
        }
    }

    /// <summary>Frames applied by the first and the last tenth of the population, which says whether being served depends on WHEN a session connected.</summary>
    public (double Head, double Tail) FramesByJoinOrder()
    {
        var tenth = Math.Max(1, _bots.Count / 10);
        var head = 0L;
        var tail = 0L;
        for (var i = 0; i < tenth; i++)
        {
            head += _bots[i].Client.Store?.Frames ?? 0;
            tail += _bots[_bots.Count - 1 - i].Client.Store?.Frames ?? 0;
        }

        return ((double)head / tenth, (double)tail / tenth);
    }

    /// <summary>How many sessions were actually granted the statistics capability they asked for.</summary>
    /// <remarks>
    /// Asking is not getting: the server grants a subset (W23). Separating "was never granted STATS" from "was granted it and is not being sent it" is the
    /// first question to settle when the blocks do not arrive, and it costs one field the client already holds.
    /// </remarks>
    public int StatsGranted
    {
        get
        {
            var granted = 0;
            foreach (var bot in _bots)
            {
                if ((bot.Client.CapsGranted & Capabilities.Stats) != 0)
                {
                    granted++;
                }
            }

            return granted;
        }
    }

    /// <summary>
    /// How many <c>STATS</c> blocks each session has received: the total, the smallest, the largest, and how many received none.
    /// </summary>
    /// <remarks>
    /// A block is emitted on a fixed cadence and rides on one frame, never retried, so the values alone cannot distinguish a server that stopped sending
    /// from one whose numbers stopped moving. Counting the blocks can. This replaced a report that took the MAX of a metric across the population, which was
    /// worse than useless: a maximum never falls, so it looked frozen whenever no new peak arrived and looked healthy whenever one did.
    /// </remarks>
    public (long Total, long Min, long Max, int Starved) StatsBlocks()
    {
        var total = 0L;
        var min = long.MaxValue;
        var max = 0L;
        var starved = 0;

        foreach (var bot in _bots)
        {
            var blocks = bot.Client.Store?.StatsBlocks ?? 0;
            total += blocks;
            min = Math.Min(min, blocks);
            max = Math.Max(max, blocks);
            if (blocks == 0)
            {
                starved++;
            }
        }

        return (total, min == long.MaxValue ? 0 : min, max, starved);
    }

    /// <summary>
    /// The lowest and highest value of a server metric across the population, and how many sessions hold the highest.
    /// </summary>
    /// <remarks>
    /// The spread is the measurement, not a debugging aid: every session's <c>STATS</c> block is a copy of the same server segment, so any spread at all
    /// means some sessions are not receiving the block the others are. One bot's reading cannot tell the difference between "the server stopped emitting"
    /// and "this session stopped being sent to", and those are different defects.
    /// </remarks>
    /// <param name="name">The metric's catalog name.</param>
    public (double Min, double Max, int AtMax) Spread(string name)
    {
        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        var atMax = 0;

        foreach (var bot in _bots)
        {
            var plan = bot.Client.Plan;
            var store = bot.Client.Store;
            if (plan == null || store == null)
            {
                continue;
            }

            foreach (var metric in plan.ServerMetrics)
            {
                if (metric.Name != name)
                {
                    continue;
                }

                var value = store.ServerMetricValues[metric.Offset][0];
                min = Math.Min(min, value);
                if (value > max)
                {
                    max = value;
                    atMax = 1;
                }
                else if (value == max)
                {
                    atMax++;
                }

                break;
            }
        }

        return double.IsNegativeInfinity(max) ? (double.NaN, double.NaN, 0) : (min, max, atMax);
    }

    /// <summary>How many times the shared timer has fired.</summary>
    public long Ticks { get; private set; }

    private readonly Dictionary<ushort, int> _disconnects = [];
    private readonly Lock _reportLock = new();
    private int _faults;

    /// <summary>Connects every bot and starts the shared driver.</summary>
    /// <param name="ct">Cancels the start.</param>
    /// <returns>How many sessions opened.</returns>
    public async Task<int> StartAsync(CancellationToken ct = default)
    {
        // The ramp's own keepalive clock. Half the client ping period, so no session crosses the server's silence bound while its neighbours connect.
        var rampPingPeriod = _options.TickHz > 0 ? TimeSpan.FromSeconds(0.5 / _options.TickHz) : TimeSpan.FromMilliseconds(125);
        var rampPing = Stopwatch.StartNew();

        for (var i = 0; i < _options.Count; i++)
        {
            var bot = new Bot(i, _options);
            bot.Client.Disconnected += (code, _) => Note(code);
            // Interlocked, because Fault is raised from each client's OWN receive loop: with N bots these are N threads, and `Faults++` is a
            // read-modify-write that silently loses updates. The report is the product here, so a counter that undercounts is the report lying.
            bot.Client.Fault += _ => Interlocked.Increment(ref _faults);

            try
            {
                await bot.Client.ConnectAsync(ct).ConfigureAwait(false);
                _bots.Add(bot);
            }
            catch (Exception)
            {
                // A handshake that failed is a disconnect with no code: counted as zero so a run that could not open its population is visible in the same
                // table as one that lost it later.
                Note(0);
                await bot.Client.DisposeAsync().ConfigureAwait(false);
            }

            if (_options.ConnectStagger > TimeSpan.Zero && i + 1 < _options.Count)
            {
                await Task.Delay(_options.ConnectStagger, ct).ConfigureAwait(false);
            }

            // Ping everyone already up, because the ramp is long enough to matter: a hundred sessions at this stagger take seconds, and a session that
            // says nothing for seconds is a session the server is entitled to treat as gone. Without this the generator manufactures its own silence and
            // then reports the server's reaction to it as a server defect.
            //
            // ON A CADENCE, not after every connect. Pinging the whole population per connection is O(n squared) sends: at the thousand sessions this class
            // is meant to reach that is half a million pings before a single measurement, which makes the generator the expensive thing its own remarks say
            // it must not be. Half a ping period is frequent enough that nobody crosses the silence bound during the ramp.
            var sinceRampPing = rampPing.Elapsed;
            if (sinceRampPing < rampPingPeriod)
            {
                continue;
            }

            rampPing.Restart();
            foreach (var connected in _bots)
            {
                try
                {
                    await connected.Client.SendPingAsync(ct).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Counted by the disconnect handler; one bot must not stop the ramp.
                }
            }
        }

        _driver = Task.Run(() => DriveAsync(_stopping.Token), CancellationToken.None);
        return _bots.Count;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_driver != null)
        {
            try
            {
                await _driver.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Shutting down; the driver has nowhere to report a failure on the way out.
            }
        }

        foreach (var bot in _bots)
        {
            await bot.Client.DisposeAsync().ConfigureAwait(false);
        }

        _bots.Clear();
        _stopping.Dispose();
    }

    /// <summary>Records one disconnect. Called from the disconnecting client's OWN receive loop, so with N bots this is N threads.</summary>
    /// <param name="code">The close code.</param>
    /// <remarks>
    /// A <see cref="Dictionary{TKey, TValue}"/> written by several threads at once corrupts or throws, and this one is what the exit code reads and the
    /// report prints — so an unsynchronised write here is the report lying rather than merely a race. The engine's own smoke fixture locks the same two
    /// counters; this file did not inherit it.
    /// </remarks>
    private void Note(ushort code)
    {
        lock (_reportLock)
        {
            _disconnects[code] = _disconnects.GetValueOrDefault(code) + 1;
        }
    }

    private async Task DriveAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, _options.TickHz)));
        while (await SafeWaitAsync(timer, ct).ConfigureAwait(false))
        {
            Ticks++;
            var moveRegions = _options.RegionEveryTicks > 0 && Ticks % _options.RegionEveryTicks == 0;

            foreach (var bot in _bots)
            {
                if (!bot.Client.IsConnected)
                {
                    continue;
                }

                try
                {
                    // The ping first: it is what keeps the session alive, and a region update that threw would otherwise take the ping with it.
                    await bot.Client.SendPingAsync(ct).ConfigureAwait(false);

                    if (moveRegions)
                    {
                        await bot.SendRegionAsync(Ticks, _options, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    // One bot's socket going away must not stop the other ninety-nine from being driven. The disconnect is counted by its own handler.
                }
            }
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>One scripted camera: a client, an orbit centre and a phase.</summary>
    private sealed class Bot
    {
        private readonly double _centreX;
        private readonly double _centreY;
        private readonly double _phase;

        public Bot(int index, BotSwarmOptions options)
        {
            // Deterministic from the index, so a run is reproducible and two bots are never in the same place. The golden ratio keeps successive angles from
            // clustering, which a plain index × constant does not.
            var golden = 2.399963229728653;
            var radius = 400 + (index % 16 * 220);
            _centreX = Math.Cos(index * golden) * radius;
            _centreY = Math.Sin(index * golden) * radius;
            _phase = index * golden;

            Client = new TyphonClient(new ClientOptions
            {
                Endpoint = options.Endpoint,
                Kind = options.Kind,

                // The swarm's own timer drives the cadence; see the class remarks.
                PingHz = 0,
                Reconnect = false,

                // STATS is opt-in per session (W23): a server emits the block only to a client that asked for it, so a load generator that does not ask
                // reports the run with every server number reading zero — which is what the first AC-1 run did.
                Caps = Capabilities.Stats,
            });
        }

        public TyphonClient Client { get; }

        public async Task SendRegionAsync(long tick, BotSwarmOptions options, CancellationToken ct)
        {
            if (Client.Plan?.CommandByName(BuiltInCommands.ClientRegion) == null)
            {
                return;
            }

            var seconds = tick / Math.Max(1.0, options.TickHz);
            var angle = _phase + (seconds * options.CameraSpeedMps / Math.Max(1.0, options.RegionRadiusM));
            var x = _centreX + (Math.Cos(angle) * options.RegionRadiusM);
            var y = _centreY + (Math.Sin(angle) * options.RegionRadiusM);

            // A square footprint around the camera: four points is the minimum a quad needs and the minimum the wire accepts is three, so this is the
            // smallest honest region rather than the smallest legal one.
            var half = options.RegionRadiusM * 0.5;
            var values = new RecordValues
            {
                [BuiltInCommands.RegionVerticesField] = FieldValue.Of(x - half, y - half, x + half, y - half, x + half, y + half, x - half, y + half),
                [BuiltInCommands.RegionAltitudeField] = FieldValue.Of(120.0),
                [BuiltInCommands.RegionBudgetField] = FieldValue.Of(options.BudgetKiBps),
            };

            await Client.SendCommandAsync(BuiltInCommands.ClientRegion, values, ct).ConfigureAwait(false);
        }
    }
}

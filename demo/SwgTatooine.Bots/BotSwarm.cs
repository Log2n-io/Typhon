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

    /// <summary>How many connects go out between two staggers; <c>0</c>, the default, means "unset".</summary>
    /// <remarks>
    /// Prefer <see cref="ConnectRatePerSecond"/>, which says the thing a run actually cares about. This stays because it is what existing command lines
    /// pass, and an explicit value still wins — which is why the default is <c>0</c> and not <c>1</c>. With <c>1</c> as the default, an operator who
    /// deliberately passed <c>--connect-batch 1</c> was indistinguishable from one who passed nothing, so the rate quietly overrode a batch that HAD been
    /// asked for. A sentinel is the only way a setting of "the same as the default" can still win.
    /// </remarks>
    public int ConnectBatch { get; init; }

    /// <summary>
    /// Sessions to open per second during the ramp; <c>0</c>, the default, leaves <see cref="ConnectBatch"/> in charge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A rate, because the batch alone could not express one.</b> Each handshake used to be awaited before the next began, so the ramp's real ceiling was
    /// one session per round trip whatever the stagger said — about sixty seconds for a thousand sessions over a sixty-millisecond link, where the stagger
    /// implied five. The batch now opens concurrently, which makes a rate meaningful: <c>batch = rate x stagger</c>.
    /// </para>
    /// <para>
    /// <b>It is a rail, not a target to maximise.</b> A server admitting sessions faster than it can bind them pays on the tick, not on the socket:
    /// <c>BindAvatars</c> spawns each avatar serially inside the tick's own transaction, and a just-admitted session is also what #1024 stalls the
    /// replication track on. Two hundred a second is the figure 07-bots § 3 names; the reason to keep it low is on the server's side.
    /// </para>
    /// </remarks>
    public int ConnectRatePerSecond { get; init; }

    /// <summary>
    /// Every this many seconds each god camera asks for the next planet (<c>ViewRealm</c>, Realms G3); 0, the default, never. Needs a server that
    /// declares the command.
    /// </summary>
    public int TourEverySeconds { get; init; }

    /// <summary>How many planets the tour cycles through: the server's <c>--planets</c>.</summary>
    public int Planets { get; init; } = 1;

    /// <summary>
    /// The bearer token bot <c>i</c> presents on <c>HELLO</c>, or <see langword="null"/> for an anonymous session.
    /// </summary>
    /// <remarks>
    /// <b>A function of the index rather than one token for the swarm, because a player session IS its account.</b> The server's
    /// admission hook validates the token and carries the account through as the thing that decides WHICH avatar to bind, so a
    /// thousand player bots are a thousand identities and not one identity a thousand times. God sessions stay anonymous and leave
    /// this null, which is why <c>--kind god</c> needs no accounts file at all.
    /// </remarks>
    public Func<int, string> TokenFor { get; init; }

    /// <summary>The resume token bot <c>i</c> presents, or <see langword="null"/> for a cold session.</summary>
    /// <remarks>
    /// Set from the accounts file, so a bot box that was restarted resumes the sessions it had rather than cold-logging a thousand
    /// avatars the server is still holding for their absent clients — 07-bots § 3's churn-storm distinction, and the reason the
    /// resume work came before the fleet work.
    /// </remarks>
    public Func<int, byte[]> ResumeFor { get; init; }

    /// <summary>Told the resume token the server minted for bot <c>i</c>, so the next run can present it.</summary>
    /// <remarks>
    /// Called on the connect loop's thread once a batch is in, never from the connect tasks: its implementation writes to the
    /// accounts file, and a thousand tasks appending to one store would be the contended thing in a generator whose whole job is
    /// not to be the expensive part.
    /// </remarks>
    public Action<int, byte[]> OnResumeToken { get; init; }
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

    /// <summary>
    /// The population, pre-sized, with <see cref="_botCount"/> saying how much of it has been published.
    /// </summary>
    /// <remarks>
    /// <b>An array and a count rather than a <c>List</c>, because the readers are on other threads.</b> Every counter this class
    /// exposes walks this collection, and the bot console calls them from a UI thread while the ramp is still appending — a plain
    /// <c>List</c> being enumerated while <c>Add</c> grows it throws "collection was modified", so watching a thousand-bot ramp
    /// crashed the page at exactly the moment an operator is looking at it. Pre-sized means an append can never reallocate, and
    /// publishing the count with a release store after writing the slot means a reader either sees a bot or does not see it,
    /// and never sees a slot that has not been filled.
    /// </remarks>
    private readonly Bot[] _bots;

    private int _botCount;
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
        _bots = new Bot[options.Count];
    }

    /// <summary>How many sessions the ramp has published so far.</summary>
    private int BotCount => Volatile.Read(ref _botCount);

    /// <summary>
    /// The published population, as a span whose length is fixed at the moment it is taken.
    /// </summary>
    /// <remarks>
    /// Taking the length once is what makes a concurrent append harmless: the span covers only slots whose write happened before
    /// the count was published, so a ramp that adds a bot during an enumeration is simply not in that enumeration. It cannot be
    /// used across an <c>await</c>, which is why the two asynchronous walks index the array directly instead.
    /// </remarks>
    private ReadOnlySpan<Bot> Bots => _bots.AsSpan(0, Volatile.Read(ref _botCount));

    /// <summary>How many sessions are open right now.</summary>
    public int Connected
    {
        get
        {
            var live = 0;
            foreach (var bot in Bots)
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
    /// <summary>Bytes delivered to the whole population, as they came off the wire.</summary>
    public long BytesReceived
    {
        get
        {
            var total = 0L;
            foreach (var bot in Bots)
            {
                total += bot.Client.BytesReceived;
            }

            return total;
        }
    }

    public long MessagesReceived
    {
        get
        {
            var total = 0L;
            foreach (var bot in Bots)
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
        foreach (var bot in Bots)
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
        foreach (var bot in Bots)
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

        foreach (var bot in Bots)
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
            foreach (var bot in Bots)
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
        // The count once, so head and tail are measured against ONE population rather than two reads of a growing
        // one.
        var live = BotCount;
        var tenth = Math.Max(1, live / 10);
        var head = 0L;
        var tail = 0L;
        for (var i = 0; i < tenth && i < live; i++)
        {
            head += _bots[i].Client.Store?.Frames ?? 0;
            tail += _bots[live - 1 - i].Client.Store?.Frames ?? 0;
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
            foreach (var bot in Bots)
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

        foreach (var bot in Bots)
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

        foreach (var bot in Bots)
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

    /// <summary>
    /// Realms G3, across the population: the tours asked for, the realm switches the sessions applied (each a <c>RESET</c> carrying a new <c>REALM</c>),
    /// and the <c>RealmNews</c> announcements heard.
    /// </summary>
    public (long Asked, long Switches, long News, int InRealm) Realms()
    {
        long asked = 0, switches = 0, news = 0;
        var inRealm = 0;
        foreach (var bot in Bots)
        {
            asked += Volatile.Read(ref bot.ToursAsked);
            switches += Volatile.Read(ref bot.RealmSwitches);
            news += bot.News.Count;
            inRealm += bot.Client.Store?.Realm != null ? 1 : 0;
        }

        return (asked, switches, news, inRealm);
    }

    private readonly Dictionary<ushort, int> _disconnects = [];
    private readonly Lock _reportLock = new();
    private int _faults;

    /// <summary>Distinct protocol faults this run produced, by message, and how many times each fired.</summary>
    /// <remarks>
    /// <b>Separate from <c>_faults</c>, which counts the network's failures.</b> These are this program asking the server for something the catalog does not
    /// describe — a field name that does not exist, a command that does not, a value of the wrong shape — and they are OUR bug, every time. Kept by message
    /// so that one typo firing ten times a second for a whole run reports as one line with a count rather than a flood, and so that the exit summary can say
    /// the run was not measuring what it claimed to.
    /// </remarks>
    private readonly Dictionary<string, int> _protocolFaults = [];

    /// <summary>Protocol faults seen this run: the distinct messages, and the total.</summary>
    public (int Distinct, int Total) ProtocolFaults()
    {
        lock (_reportLock)
        {
            var total = 0;
            foreach (var n in _protocolFaults.Values)
            {
                total += n;
            }

            return (_protocolFaults.Count, total);
        }
    }

    /// <summary>Writes the protocol faults to the console, or nothing when there were none.</summary>
    public void ReportProtocolFaults()
    {
        lock (_reportLock)
        {
            if (_protocolFaults.Count == 0)
            {
                return;
            }

            Console.WriteLine($"  !! {_protocolFaults.Count} PROTOCOL FAULT(S) — this run did not send what it believed it was sending:");
            foreach (var (message, count) in _protocolFaults)
            {
                Console.WriteLine($"     x{count}  {message}");
            }
        }
    }

    /// <summary>
    /// Records a fault caused by what this program sent, printing the first of each kind.
    /// </summary>
    /// <param name="e">The encoder's complaint.</param>
    private void NoteProtocolFault(Exception e)
    {
        var message = $"{e.GetType().Name}: {e.Message}";
        lock (_reportLock)
        {
            if (_protocolFaults.TryGetValue(message, out var seen))
            {
                _protocolFaults[message] = seen + 1;
                return;
            }

            _protocolFaults[message] = 1;
        }

        // The FIRST of each kind, immediately: a fleet that hides its own send bug until the summary has already spent the run measuring nothing.
        Console.WriteLine($"  !! protocol fault (this is our bug, not the server's): {message}");
    }

    /// <summary>Connects every bot and starts the shared driver.</summary>
    /// <param name="ct">Cancels the start.</param>
    /// <returns>How many sessions opened.</returns>
    public async Task<int> StartAsync(CancellationToken ct = default)
    {
        // The ramp's own keepalive clock. Half the client ping period, so no session crosses the server's silence bound while its neighbours connect.
        var rampPingPeriod = _options.TickHz > 0 ? TimeSpan.FromSeconds(0.5 / _options.TickHz) : TimeSpan.FromMilliseconds(125);
        var rampPing = Stopwatch.StartNew();

        // `batch = rate x stagger` when a rate was asked for, so --connect-rate says sessions per second and the stagger stays the quantum it always was.
        // An explicit --connect-batch wins, which keeps every existing command line meaning what it meant.
        var batch = Math.Max(1, _options.ConnectBatch);
        if (_options.ConnectBatch <= 0 && _options.ConnectRatePerSecond > 0 && _options.ConnectStagger > TimeSpan.Zero)
        {
            batch = Math.Max(1, (int)Math.Ceiling(_options.ConnectRatePerSecond * _options.ConnectStagger.TotalSeconds));
        }

        // The deadline the pacing above is measured against. `perBatch` is what the operator asked for expressed as a period:
        // at --connect-rate 200 and a batch of 4 that is 20 ms, and a batch that took longer than that simply does not wait.
        var stopwatch = Stopwatch.StartNew();
        var perBatch = _options.ConnectRatePerSecond > 0
            ? TimeSpan.FromSeconds((double)batch / _options.ConnectRatePerSecond)
            : _options.ConnectStagger;
        var nextBatchDue = TimeSpan.Zero;

        for (var i = 0; i < _options.Count; i += batch)
        {
            nextBatchDue += perBatch;

            // CONCURRENTLY within the batch. Awaited one at a time — as this loop did until now — the ramp's ceiling is one session per handshake round
            // trip, which no stagger can lift: a thousand sessions over a 60 ms link took a minute while --connect-batch 25 claimed to ask for 1250/s.
            var size = Math.Min(batch, _options.Count - i);
            var opening = new Task<Bot>[size];
            for (var k = 0; k < size; k++)
            {
                opening[k] = OpenOneAsync(i + k, ct);
            }

            var opened = await Task.WhenAll(opening).ConfigureAwait(false);

            // Appended on THIS thread, after the batch, and the slot is written before the count is published so a reader on the
            // control panel's thread never sees an unfilled one.
            foreach (var bot in opened)
            {
                if (bot != null)
                {
                    bot.WatchRealm();

                    // After the handshake, because the token arrives in WELCOME: asking before the connect would persist the token
                    // the LAST run was given, which is the one that has just been spent.
                    _options.OnResumeToken?.Invoke(bot.Index, bot.Client.ResumeToken);

                    var at = _botCount;
                    _bots[at] = bot;
                    Volatile.Write(ref _botCount, at + 1);
                }
            }

            if (i + size < _options.Count)
            {
                // PACED AGAINST A DEADLINE, not slept for after the fact. Delaying a fixed stagger after awaiting the batch makes
                // the period `handshake + stagger` rather than `max(handshake, stagger)`, so the achieved rate was
                // `size / (stagger + RTT)`: on the 60 ms link this tool exists to measure over, `--connect-rate 200` delivered
                // about fifty a second while reporting that it had asked for two hundred. For a load generator whose report IS
                // the product, a knob that overstates by four times is worse than no knob.
                var remaining = nextBatchDue - stopwatch.Elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, ct).ConfigureAwait(false);
                }
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
            await SweepRampPingsAsync(ct).ConfigureAwait(false);
        }

        _driver = Task.Run(() => DriveAsync(_stopping.Token), CancellationToken.None);
        return BotCount;
    }

    /// <summary>Opens one bot's session, or answers <see langword="null"/> when the handshake failed.</summary>
    /// <param name="index">The bot's index in the population.</param>
    /// <param name="ct">Cancels the connect.</param>
    /// <returns>The connected bot, or <see langword="null"/>.</returns>
    /// <remarks>
    /// Separated out so a batch of these can run at once. It does NOT touch <c>_bots</c>: the caller appends, on its own thread, once the batch is in.
    /// </remarks>
    private async Task<Bot> OpenOneAsync(int index, CancellationToken ct)
    {
        var bot = new Bot(index, _options);
        bot.Client.Disconnected += (code, _) => Note(code);

        // Interlocked, because Fault is raised from each client's OWN receive loop: with N bots these are N threads, and `Faults++` is a
        // read-modify-write that silently loses updates. The report is the product here, so a counter that undercounts is the report lying.
        bot.Client.Fault += _ => Interlocked.Increment(ref _faults);

        try
        {
            await bot.Client.ConnectAsync(ct).ConfigureAwait(false);
            return bot;
        }
        catch (Exception)
        {
            // A handshake that failed is a disconnect with no code: counted as zero so a run that could not open its population is visible in the same
            // table as one that lost it later.
            Note(0);
            await bot.Client.DisposeAsync().ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>Pings every session already up, so none crosses the server's silence bound while its neighbours are still connecting.</summary>
    /// <param name="ct">Cancels the sweep.</param>
    /// <remarks>
    /// <b>Concurrently.</b> Awaited one at a time this is O(n) round trips per cadence, and at the thousand sessions this class exists to reach the sweep
    /// took longer than the cadence it was supposed to fit inside — so the ramp spent its time pinging rather than connecting, and the population the
    /// generator was measuring arrived late for reasons that were entirely its own.
    /// </remarks>
    private async Task SweepRampPingsAsync(CancellationToken ct)
    {
        var live = BotCount;
        if (live == 0)
        {
            return;
        }

        var pings = new Task[live];
        for (var i = 0; i < live; i++)
        {
            pings[i] = PingOneAsync(_bots[i], ct);
        }

        await Task.WhenAll(pings).ConfigureAwait(false);
    }

    /// <summary>Pings one session, swallowing its failure.</summary>
    /// <param name="bot">The bot.</param>
    /// <param name="ct">Cancels the send.</param>
    private static async Task PingOneAsync(Bot bot, CancellationToken ct)
    {
        try
        {
            await bot.Client.SendPingAsync(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Counted by the disconnect handler; one bot must not stop the ramp.
        }
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

        // Indexed, not a span: a ref struct cannot live across an await.
        var live = BotCount;
        for (var i = 0; i < live; i++)
        {
            await _bots[i].Client.DisposeAsync().ConfigureAwait(false);
        }

        Volatile.Write(ref _botCount, 0);
        Array.Clear(_bots);
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
            var tourPeriod = (long)_options.TourEverySeconds * Math.Max(1, _options.TickHz);
            var touring = _options.TourEverySeconds > 0 && _options.Planets > 1 && _options.Kind != "player";
            var index = 0;

            // Indexed for the same reason DisposeAsync is: the body awaits.
            var live = BotCount;
            for (var b = 0; b < live; b++)
            {
                var bot = _bots[b];
                index++;
                if (!bot.Client.IsConnected)
                {
                    continue;
                }

                try
                {
                    // The ping first: it is what keeps the session alive, and a region update that threw would otherwise take the ping with it.
                    await bot.Client.SendPingAsync(ct).ConfigureAwait(false);

                    // Staggered by bot: every camera switching on one tick would be N whole-planet RESETs in one server tick.
                    if (touring && (Ticks + index) % tourPeriod == 0)
                    {
                        await bot.SendNextPlanetAsync(_options, ct).ConfigureAwait(false);
                    }

                    if (moveRegions)
                    {
                        await bot.SendRegionAsync(Ticks, _options, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException or FormatException)
                {
                    // <b>OUR OWN bug, not the network's, and it must not be survivable in silence.</b> These are what the encoder throws when this program
                    // asks for something the catalog does not describe: a field name that does not exist (RecordValues.For), a command name that does not
                    // (SendCommandAsync), a value of the wrong shape. The blanket catch below used to take them too, so --god-tour sent nothing for its whole
                    // life while every counter said the run was healthy. At a thousand bots that is the difference between a load test and a screensaver.
                    //
                    // Counted and printed once per distinct message rather than thrown: one bot cannot be allowed to stop the fleet, and a fleet that stops
                    // on the first typo tells you less than one that reports it and keeps load on the server. The run's exit summary carries the count.
                    NoteProtocolFault(e);
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

    /// <summary>Counts the <c>RealmNews</c> events a session hears; every other event is dropped. On the session's receive loop.</summary>
    private sealed class NewsCounter : IEventHandler
    {
        private long _count;

        public long Count => Volatile.Read(ref _count);

        public void Event(MessagePlan type)
        {
            if (type.Name == "RealmNews")
            {
                Interlocked.Increment(ref _count);
            }
        }

        public void Number(FieldPlan field, scoped ReadOnlySpan<double> components)
        {
        }

        public void Integer64(FieldPlan field, scoped ReadOnlySpan<ulong> components)
        {
        }

        public void Text(FieldPlan field, scoped ReadOnlySpan<byte> utf8)
        {
        }

        public void Bytes(FieldPlan field, scoped ReadOnlySpan<byte> bytes)
        {
        }

        public void List(FieldPlan field, int count, scoped ReadOnlySpan<double> components)
        {
        }
    }

    /// <summary>One scripted camera: a client, an orbit centre and a phase.</summary>
    private sealed class Bot
    {
        private readonly double _centreX;
        private readonly double _centreY;
        private readonly double _phase;
        private int _planet;

        public long ToursAsked;
        public long RealmSwitches;
        public readonly NewsCounter News = new();

        public Bot(int index, BotSwarmOptions options)
        {
            // Deterministic from the index, so a run is reproducible and two bots are never in the same place. The golden ratio keeps successive angles from
            // clustering, which a plain index × constant does not.
            var golden = 2.399963229728653;
            var radius = 400 + (index % 16 * 220);
            _centreX = Math.Cos(index * golden) * radius;
            _centreY = Math.Sin(index * golden) * radius;
            _phase = index * golden;

            Index = index;
            Client = new TyphonClient(new ClientOptions
            {
                Endpoint = options.Endpoint,
                Kind = options.Kind,

                // Empty, not null: ClientOptions.Token defaults to "" and the handshake writes it unconditionally.
                Token = options.TokenFor?.Invoke(index) ?? string.Empty,
                ResumeToken = options.ResumeFor?.Invoke(index),

                // The swarm's own timer drives the cadence; see the class remarks.
                PingHz = 0,
                Reconnect = false,

                // A bot applies every frame and evaluates no motion, so it keeps one segment per entity instead of the 17 a 300 ms render delay needs at
                // 50 Hz. At d07 a bot holds ~25 000 entities, and the history was ~20 MB of each bot's ~32 MB.
                SegmentHistory = 1,

                // STATS is opt-in per session (W23): a server emits the block only to a client that asked for it, so a load generator that does not ask
                // reports the run with every server number reading zero — which is what the first AC-1 run did.
                Caps = Capabilities.Stats,
                Events = News,
            });
        }

        public TyphonClient Client { get; }

        /// <summary>This bot's index in the population, which is what its account and its file line are keyed on.</summary>
        public int Index { get; }

        /// <summary>Counts the realm switches the session applies. After the connect, when the store exists; the swarm does not reconnect.</summary>
        public void WatchRealm()
        {
            if (Client.Store != null)
            {
                Client.Store.RealmChanged += (previous, current) =>
                {
                    if (previous != null && current != null && previous.RealmId != current.RealmId)
                    {
                        Interlocked.Increment(ref RealmSwitches);
                    }
                };
            }
        }

        /// <summary>Asks for the next planet (Realms G3): the server moves the camera, and its next frame is a RESET over that planet's REALM.</summary>
        public async Task SendNextPlanetAsync(BotSwarmOptions options, CancellationToken ct)
        {
            if (Client.Plan?.CommandByName("ViewRealm") == null)
            {
                return;
            }

            _planet = (_planet + 1) % options.Planets;
            // "realm", as the server declares it (TatooineReplication.cs: Field(v => v.Realm, Codec.VarUInt, "realm")). It read "Realm" for as long as
            // --god-tour has existed, so every tour silently asked for nothing: RecordValues.For threw, and the driver's catch below ate it once per tick.
            var values = new RecordValues { ["realm"] = FieldValue.Of((double)_planet) };
            await Client.SendCommandAsync("ViewRealm", values, ct).ConfigureAwait(false);
            Interlocked.Increment(ref ToursAsked);
        }

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
            // Vertices are list<pos3> over the session's realm (typhon.3): a flat realm's quad at z 0, a deep realm's as a box half as tall as it is wide.
            var half = options.RegionRadiusM * 0.5;
            double[] vertices = Client.Store?.Realm?.Deep == true
                ?
                [
                    x - half, y - half, -half, x + half, y - half, -half, x + half, y + half, -half, x - half, y + half, -half,
                    x - half, y - half, half, x + half, y - half, half, x + half, y + half, half, x - half, y + half, half,
                ]
                : [x - half, y - half, 0, x + half, y - half, 0, x + half, y + half, 0, x - half, y + half, 0];
            var values = new RecordValues
            {
                [BuiltInCommands.RegionVerticesField] = FieldValue.Of(vertices),
                [BuiltInCommands.RegionAltitudeField] = FieldValue.Of(120.0),
                [BuiltInCommands.RegionBudgetField] = FieldValue.Of(options.BudgetKiBps),
            };

            await Client.SendCommandAsync(BuiltInCommands.ClientRegion, values, ct).ConfigureAwait(false);
        }
    }
}

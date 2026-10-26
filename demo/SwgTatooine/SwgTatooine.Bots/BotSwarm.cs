using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SwgTatooine.Bots.Brain;
using Typhon.Client;
using Typhon.Protocol;
using SwgTatooine.Navigation;

namespace SwgTatooine.Bots;

/// <summary>What a swarm needs to know.</summary>
public sealed class BotSwarmOptions
{
    /// <summary>Where the server is: <c>ws://…</c> or <c>tcp://host:port</c>.</summary>
    public Uri Endpoint { get; init; }

    /// <summary>How many sessions to open to begin with.</summary>
    public int Count { get; init; } = 100;

    /// <summary>
    /// The largest population the swarm may later be turned up to; <c>0</c> or less means <see cref="Count"/> is also the ceiling.
    /// </summary>
    /// <remarks>
    /// <b>Capacity is the session array's size and the dial's ceiling, and costs an allocation.</b> It used to also be the number of
    /// accounts signed up before the run started, which made asking for headroom cost its full price immediately; accounts are now
    /// provisioned a chunk at a time as the ramp reaches them, so a ceiling of ten thousand with five hundred bots signs up about five
    /// hundred.
    /// </remarks>
    public int MaxCount { get; init; }

    /// <summary>Bots carried through provision → connect → drive as one step; 0 or less means 250.</summary>
    /// <remarks>
    /// Large enough that per-chunk overhead is noise, small enough that the first bots are walking in about a second and a ten
    /// thousand ramp is forty steps rather than twenty thousand.
    /// </remarks>
    public int ChunkSize { get; init; } = 250;

    /// <summary>
    /// Ensures accounts exist for the bots at <c>[from, from + count)</c>, answering how many of them are usable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Supplied by the caller rather than built here, because the swarm must not know what an account is.</b> It drives sessions;
    /// the provisioner owns HTTP, the key and the accounts file. Null means there is nothing to provision — which is the <c>god</c>
    /// case, where a session is anonymous and the chunk can be opened straight away.
    /// </para>
    /// <para>
    /// Answering a COUNT rather than a bool is what lets a partial chunk still open: if 250 were asked for and 180 signed up, the
    /// reconciler opens 180 and comes back for the rest, instead of discarding a chunk because one account in it failed.
    /// </para>
    /// </remarks>
    public Func<int, int, CancellationToken, Task<int>> EnsureAccounts { get; init; }

    /// <summary>The session kind the server's admission expects.</summary>
    public string Kind { get; init; } = "god";

    /// <summary>How often the shared timer fires, in hertz. Pings and region updates are both derived from it.</summary>
    public int TickHz { get; init; } = 4;

    /// <summary>How far a camera's footprint reaches, in metres.</summary>
    public double RegionRadiusM { get; init; } = 256;

    /// <summary>
    /// Seconds a wandering player holds one heading before picking another; <c>0</c> leaves the avatars standing still.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Player bots stood perfectly still until this existed, and that quietly halved what a run measured.</b> The swarm sent one
    /// command in its whole life — the god camera's <c>ClientRegion</c>, which <c>--kind player</c> does not even use — so a thousand
    /// player sessions exercised admission, subscription and replication and touched the intent path, the spatial index and cluster
    /// migration not at all. The server's own report said so and it took a person looking at the world to notice: <c>intents: 5
    /// applied</c> while a thousand avatars were bound, those five being the one human in a browser.
    /// </para>
    /// <para>
    /// This is not the brain <c>07-bots</c> § 2 describes — no goals, no profiles, no reaction latency, no adversarial slice. It walks,
    /// which is the one behaviour that makes bodies cross cells.
    /// </para>
    /// </remarks>
    public double WanderSeconds { get; init; } = 12;

    /// <summary>The speed class a wandering player asks for; 2 is <c>Run</c>, the server's default gait.</summary>
    public byte WanderSpeedClass { get; init; } = 2;

    /// <summary>Whether player bots decide for themselves from what they are sent, instead of wandering on a scripted heading.</summary>
    /// <remarks>
    /// <para>
    /// <b>Off by default, because it is a different workload and not a better version of the same one.</b> A wandering bot
    /// walks a random course forever, which loads the spatial index evenly and is what every existing measurement was
    /// taken against. A thinking bot travels between cities, stands still in them, hunts what it finds and takes
    /// shuttles — so it clumps, it idles, and its interest set changes shape. Flipping this silently would make two runs
    /// incomparable while both reported a median.
    /// </para>
    /// <para>
    /// It replaces wandering rather than adding to it: two things steering one body would fight over the heading.
    /// </para>
    /// </remarks>
    public bool Brain { get; init; }

    /// <summary>
    /// How often a thinking bot tips a named player standing near it, seconds — each bot on its own jittered period, half to one and a half times
    /// this (economy-foundation.md E0-11). 0, the default, never tips: a tip is a ledger row, so a run with tips is a different workload.
    /// </summary>
    public double TipSeconds { get; init; }

    /// <summary>
    /// Whether a thinking bot takes missions at the terminals it visits (lair-objectives.md B-1). On by default with <c>--brain</c>; <c>--no-missions</c>
    /// keeps a measurement run's workload what it was — a bot served the measurement profile never sees a lair, and would ride out to stand there.
    /// </summary>
    public bool Missions { get; init; } = true;

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

    /// <summary>Told something an operator needs to read, once, in words rather than as a counter.</summary>
    /// <remarks>
    /// For the conditions a number cannot express on its own. "Replaced 21 665 sessions" is a counter; "every session is dying as
    /// fast as it opens, so the dial has stopped trying at full speed" is the sentence that stops somebody staring at a panel for
    /// five minutes wondering why the population will not move.
    /// </remarks>
    public Action<string> OnNote { get; init; }
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

    /// <summary>
    /// The movement command's catalog name, which the SWG server declares and both SDKs resolve by this exact string.
    /// </summary>
    /// <remarks>
    /// A constant rather than a literal at the two call sites: <c>SendCommandAsync</c> throws for a name the catalog does not
    /// describe, and <c>DriveAsync</c> catches that as "our own bug" and counts it — so a typo would cost a fleet that reports
    /// protocol faults rather than one that fails to compile.
    /// </remarks>
    private const string MoveDirCommand = "MoveDir";

    /// <summary>The commands a thinking bot sends besides a heading, named as the server declares them.</summary>
    private const string SetTargetCommand = "SetTarget";

    private const string BoardShuttleCommand = "BoardShuttle";

    private const string MoveToCommand = "MoveTo";

    private const string SetMountCommand = "SetMount";

    /// <summary>The server's combat stance command; a bot hunts with the pistol, the one stance that reaches from <c>BotBrain.StrikeM</c>.</summary>
    private const string SetStanceCommand = "SetStance";

    /// <summary>The economy's player-to-player transfer (economy-foundation.md E0-7).</summary>
    private const string GiveCreditsCommand = "GiveCredits";

    /// <summary>A mission taken at a terminal, and one given up (lair-objectives.md B-1, B-4).</summary>
    private const string TakeMissionCommand = "TakeMission";

    private const string AbandonMissionCommand = "AbandonMission";

    /// <summary>How near the bot an offer is taken from: a terminal's reach (<c>TatooineData.MissionTerminalReachM</c>).</summary>
    private const float OfferReachM = 6f;

    /// <summary>How near the journal's point a lair in view is taken for the mission's own.</summary>
    private const float MissionLairMatchM = 30f;

    /// <summary>How near a bot looks for somebody to tip, metres: inside the server's 20 m, since the bot reads a position a little old.</summary>
    private const float TipReachM = 15f;

    /// <summary>The most one bot tip gives: small, so a fleet's tips churn the ledger without draining anybody's 1 000 credits.</summary>
    private const int MaxBotTip = 50;

    /// <summary><c>Stances.Pistol</c> on the server.</summary>
    private const double PistolStance = 3.0;

    /// <summary>
    /// The public content a thinking bot walks a city by — walls, doors, terminals — read once per fleet from the server's HTTP surface, as a browser
    /// reads it. Null until loaded, and for good when the server publishes none: the brains then travel and hunt as they always did.
    /// </summary>
    private BotAssets _assets;

    /// <summary>Bots driven concurrently within one tick.</summary>
    /// <remarks>
    /// Large enough that the per-tick cost is a few round trips rather than N of them, small enough that the in-flight sends and their
    /// buffers stay bounded: at ten thousand sessions this is twenty batches, where the sequential loop was ten thousand.
    /// </remarks>
    private const int DriveBatch = 512;

    /// <summary>Serialises the slot a freshly opened bot takes, because handshakes complete concurrently.</summary>
    private readonly object _appendLock = new();

    private long _pingTicksTotal;
    private long _pingTicksWorst;
    private long _pingSends;

    private double _pingSweepMs;
    private double _pingSweepWorstMs;
    private double _driveSweepMs;
    private double _driveSweepWorstMs;

    private int _botCount;
    private readonly CancellationTokenSource _stopping = new();
    private Task _driver;
    private Task _pinger;
    private Task _reconciler;
    private Task _statsPoller;

    /// <summary>How long the reconciler waits between polls when the population already matches the target.</summary>
    private static readonly TimeSpan IdlePoll = TimeSpan.FromMilliseconds(100);

    /// <summary>How long it waits between polls once the ramp has stopped making progress.</summary>
    /// <remarks>
    /// Long enough that a target the server refuses stops being a load test of its own, short enough that lowering the target or a
    /// server recovering is picked up while an operator is still looking at the page.
    /// </remarks>
    private static readonly TimeSpan StalledPoll = TimeSpan.FromSeconds(2);

    /// <summary>Sessions a churning fleet opens per round, instead of a whole chunk.</summary>
    /// <remarks>
    /// Small enough that a hopeless retry costs the server almost nothing, large enough that one round is a fair test of whether
    /// the condition has cleared — a single session can fail for its own reasons, sixteen failing together is the condition.
    /// </remarks>
    private const int ChurnProbe = 16;

    /// <summary>The population asked for, which an operator may move while the swarm runs.</summary>
    private int _target;

    /// <summary>Sessions found closed and taken out of the population, which the reconciler then re-opens.</summary>
    /// <remarks>
    /// Counted apart from <see cref="_retired"/> because the two mean opposite things: a shrink is an operator's decision, and this
    /// is a loss the fleet is recovering from. A run whose target never moved and whose figure here is thousands held its population
    /// by replacing it, which is a different claim from having kept it.
    /// </remarks>
    private long _retiredDead;

    /// <summary>What the server's own stats endpoint last said, under <see cref="_reportLock"/>.</summary>
    /// <remarks>
    /// <b>Polled over HTTP because the wire catalog does not carry it.</b> The close reasons that decide whether a fleet keeps its
    /// population — a session closed for silence, a session closed for lagging — are published at <c>/typhon/stats.json</c> and
    /// nowhere else, so a client that reads only replicated metrics sees the 1006 the close drain leaves behind and has to guess.
    /// Guessing cost an afternoon once; it is one GET every two seconds.
    /// </remarks>
    private long _serverClosedSilent;
    private long _serverClosedLagging;
    private long _serverSessionsSeen;
    private string _serverStatsError;

    /// <summary>Sessions closed by a shrink, so a report can tell a deliberate cut from a disconnect.</summary>
    private long _retired;

    /// <summary>Abnormal closes by their local cause, under <see cref="_reportLock"/>.</summary>
    private readonly Dictionary<string, int> _closeDetails = [];

    private BotIndexPool _indices;

    /// <summary>What the ramp's chunks cost, by half, under <see cref="_reportLock"/>.</summary>
    private int _chunks;
    private int _chunksBoundByProvisioning;
    private double _provisionMsTotal;
    private double _connectMsTotal;

    /// <summary>Completes when the population first reaches the target, or when opening stops making progress.</summary>
    private readonly TaskCompletionSource _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Sessions the ramp asked for and could not open.</summary>
    private long _openFailures;

    /// <summary>The first reason a session could not be opened, which is the one worth printing.</summary>
    private string _firstOpenFailure;

    /// <summary>Builds a swarm. Nothing connects until <see cref="StartAsync"/>.</summary>
    /// <param name="options">The swarm's configuration.</param>
    public BotSwarm(BotSwarmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Endpoint, nameof(options.Endpoint));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Count, nameof(options.Count));

        _options = options;

        // Capacity, not population: the array is the ceiling an operator may turn the fleet up to, and it is allocated once because a
        // growing array would have to be republished under the driver's feet every time somebody moved a slider.
        _bots = new Bot[Math.Max(options.Count, options.MaxCount)];
        _indices = new BotIndexPool(_bots.Length);
        _target = options.Count;
    }

    /// <summary>
    /// The population asked for. Settable while the swarm runs; the reconciler opens or closes sessions to match at the connect rate.
    /// </summary>
    /// <remarks>
    /// <b>Clamped to the capacity rather than refused</b>, because this is driven by a number typed into a form: asking for more than
    /// the swarm was built to hold should give the most it can, and the snapshot reports both figures so the difference is visible.
    /// </remarks>
    public int Target
    {
        get => Volatile.Read(ref _target);
        set => Volatile.Write(ref _target, Math.Clamp(value, 0, _bots.Length));
    }

    /// <summary>The largest population this swarm can be turned up to.</summary>
    public int Capacity => _bots.Length;

    /// <summary>Sessions the fleet found closed and replaced, which is what a dial recovering from a loss looks like.</summary>
    public long RetiredDead => Interlocked.Read(ref _retiredDead);

    /// <summary>
    /// What the server says about closes it made, and how many sessions it holds: silence closes, lagging closes, its own count.
    /// </summary>
    /// <remarks>
    /// The first of these is the number no report in this repository used to read. A fleet losing sessions sees 1006 — an abnormal
    /// close with no code — because the server's 4001 is overtaken by its close drain; the server knows it closed them for silence
    /// and says so here. Never diagnose a lost population from close codes while this is available.
    /// </remarks>
    public (long ClosedSilent, long ClosedLagging, long Sessions, string Error) ServerCloses()
    {
        lock (_reportLock)
        {
            return (_serverClosedSilent, _serverClosedLagging, _serverSessionsSeen, _serverStatsError);
        }
    }

    /// <summary>Sessions closed by a shrink rather than lost.</summary>
    public long Retired => Interlocked.Read(ref _retired);

    /// <summary>How many sessions the ramp has published so far.</summary>
    private int BotCount => Volatile.Read(ref _botCount);

    /// <summary>Sessions asked for that never opened, and why the first of them did not.</summary>
    /// <remarks>
    /// Exposed so the report and the control panel can say "asked 10 000, opened 8 192, refused 1 808 — the session table is full"
    /// instead of printing two numbers and leaving the gap to be guessed at.
    /// </remarks>
    public (long Count, string FirstReason) OpenFailures => (Interlocked.Read(ref _openFailures), Volatile.Read(ref _firstOpenFailure));

    /// <summary>
    /// The published population, as a span whose length is fixed at the moment it is taken.
    /// </summary>
    /// <remarks>
    /// Taking the length once is what makes a concurrent append harmless: the span covers only slots whose write happened before
    /// the count was published, so a ramp that adds a bot during an enumeration is simply not in that enumeration. It cannot be
    /// used across an <c>await</c>, which is why the two asynchronous walks index the array directly instead.
    /// </remarks>
    /// <summary>
    /// The live population as a span — <b>whose entries may be null, and every reader must say so.</b>
    /// </summary>
    /// <remarks>
    /// A shrink lowers the count and then empties the slots above it, so a reader that captured the count a moment earlier is walking
    /// slots that are being emptied under it. No ordering of those two writes removes that window, so tolerating a null is the
    /// reader's job: the alternative is a NullReferenceException out of the control panel's once-a-second snapshot, which takes the
    /// Blazor circuit with it — the exact failure the array-and-count design was adopted to prevent.
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
                if (bot == null)
                {
                    continue;   // a shrink can empty a slot inside a span already taken; see Bots
                }

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

    /// <summary>Bytes delivered to the whole population, as they came off the wire.</summary>
    public long BytesReceived
    {
        get
        {
            var total = 0L;
            foreach (var bot in Bots)
            {
                if (bot == null)
                {
                    continue;   // a shrink can empty a slot inside a span already taken; see Bots
                }

                total += bot.Client.BytesReceived;
            }

            return total;
        }
    }

    /// <summary>Messages received across every session.</summary>
    public long MessagesReceived
    {
        get
        {
            var total = 0L;
            foreach (var bot in Bots)
            {
                if (bot == null)
                {
                    continue;   // a shrink can empty a slot inside a span already taken; see Bots
                }

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
            if (bot == null)
            {
                continue;   // a shrink can empty a slot inside a span already taken; see Bots
            }

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
            if (bot == null)
            {
                continue;   // a shrink can empty a slot inside a span already taken; see Bots
            }

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
            if (bot == null)
            {
                continue;   // a shrink can empty a slot inside a span already taken; see Bots
            }

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
                if (bot == null)
                {
                    continue;   // a shrink can empty a slot inside a span already taken; see Bots
                }

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
            // Null-checked for the reason Bots is: a shrink empties the tail's slots, and the TAIL is exactly what this reads.
            head += _bots[i]?.Client.Store?.Frames ?? 0;
            tail += _bots[live - 1 - i]?.Client.Store?.Frames ?? 0;
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
                if (bot == null)
                {
                    continue;   // a shrink can empty a slot inside a span already taken; see Bots
                }

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
            if (bot == null)
            {
                continue;   // a shrink can empty a slot inside a span already taken; see Bots
            }

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
            if (bot == null)
            {
                continue;   // a shrink can empty a slot inside a span already taken; see Bots
            }

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

    /// <summary>The last ping sweep's duration in milliseconds, and the worst one this run has seen.</summary>
    /// <remarks>
    /// <b>Against the server's silence window, which is what makes it a reading rather than a statistic.</b> A session the fleet
    /// has not pinged within <c>3 / pingHz</c> seconds is closed with 4001, so a sweep approaching that figure means the
    /// population is about to stop growing — and the worst is kept because the live value forgets the spike that did the damage.
    /// </remarks>
    public (double Last, double Worst) PingSweepMs
    {
        get
        {
            lock (_reportLock)
            {
                return (_pingSweepMs, _pingSweepWorstMs);
            }
        }
    }

    /// <summary>The last drive sweep's duration in milliseconds — movement and regions, with the ping taken out of it.</summary>
    public (double Last, double Worst) DriveSweepMs
    {
        get
        {
            lock (_reportLock)
            {
                return (_driveSweepMs, _driveSweepWorstMs);
            }
        }
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
            if (bot == null)
            {
                continue;   // a shrink can empty a slot inside a span already taken; see Bots
            }

            asked += Volatile.Read(ref bot.ToursAsked);
            switches += Volatile.Read(ref bot.RealmSwitches);
            news += bot.News.Count;

            // <b>LIVE sessions only, which is the whole point of the number.</b> A bot whose link died keeps its store, and its
            // store keeps the last realm it was told about, so counting every store counted the dead: a panel showing
            // "holding a realm 8000 of 4314" was reporting that 3 686 corpses were still being served. The comparison is
            // against the connected count, so the two sides have to mean the same thing.
            inRealm += bot.Client.IsConnected && bot.Client.Store?.Realm != null ? 1 : 0;
        }

        return (asked, switches, news, inRealm);
    }

    /// <summary>Movement intents this fleet has sent, so a run can prove its population was walking rather than standing.</summary>
    /// <returns>The count.</returns>
    /// <remarks>
    /// <b>Worth its own counter because its absence was invisible.</b> Every run before the wander existed reported a healthy fleet
    /// of a thousand bound avatars that sent not one intent, and the only way to notice was to look at the world in a browser. A
    /// number here, next to the sessions, makes "they are standing still" a thing the report says rather than a thing you see.
    /// </remarks>
    /// <summary>The missions the thinking bots asked for, took (their journal said so), completed (paid) and cancelled.</summary>
    /// <returns>The four counts, over the fleet.</returns>
    public (long Asked, long Taken, long Completed, long Cancelled) Missions()
    {
        long asked = 0;
        long taken = 0;
        long completed = 0;
        long cancelled = 0;
        foreach (var bot in Bots)
        {
            if (bot == null)
            {
                continue;
            }

            asked += Volatile.Read(ref bot.MissionTakesSent);
            taken += Volatile.Read(ref bot.MissionsTaken);
            completed += Volatile.Read(ref bot.MissionsCompleted);
            cancelled += Volatile.Read(ref bot.MissionsCancelled);
        }

        return (asked, taken, completed, cancelled);
    }

    /// <summary>The tips the thinking bots sent, and the times a tip was due with nobody named within reach.</summary>
    /// <returns>Both counts, over the fleet.</returns>
    public (long Sent, long NobodyNear) Tips()
    {
        long sent = 0;
        long nobody = 0;
        foreach (var bot in Bots)
        {
            if (bot == null)
            {
                continue;
            }

            sent += Volatile.Read(ref bot.TipsSent);
            nobody += Volatile.Read(ref bot.TipsNobodyNear);
        }

        return (sent, nobody);
    }

    public long IntentsSent()
    {
        long sent = 0;
        foreach (var bot in Bots)
        {
            if (bot == null)
            {
                continue;   // a shrink can empty a slot inside a span already taken; see Bots
            }

            sent += Volatile.Read(ref bot.IntentsSent);
        }

        return sent;
    }

    /// <summary>What the brains decided, which is the only evidence a thinking run produces that a wandering one does not.</summary>
    /// <returns>Shuttle boardings asked for, and hunts begun.</returns>
    /// <remarks>
    /// <b>Counted for the reason the movement intents are: the absence was invisible.</b> A fleet whose brains never chose to travel
    /// or never reached a port looks exactly like a healthy fleet of walkers, and the first version of this sent two intents per
    /// session in 45 seconds while every number in the report said the run was fine.
    /// </remarks>
    public (long Boardings, long Hunts, int PortsKnownMax, int BotsThatCanFly, int Creatures) Decisions()
    {
        long boardings = 0;
        long hunts = 0;
        var portsMax = 0;
        var canFly = 0;
        var creatures = 0;
        foreach (var bot in Bots)
        {
            if (bot == null)
            {
                continue;
            }

            boardings += Volatile.Read(ref bot.BoardingsAsked);
            hunts += Volatile.Read(ref bot.HuntsStarted);

            // What the brain has LEARNED, which is the precondition of the two counters above and the thing their
            // absence does not distinguish from a brain that is working. A fleet with no boardings because no bot knows
            // two ports is a different report from one that knows them and never chooses to fly, and both read as 0.
            var ports = bot.PortsKnown();
            portsMax = Math.Max(portsMax, ports);
            if (ports >= 2)
            {
                canFly++;
            }

            creatures = Math.Max(creatures, Volatile.Read(ref bot.CreaturesSeenMax));
        }

        return (boardings, hunts, portsMax, canFly, creatures);
    }

    /// <summary>What the brains did with the cities, summed over the fleet: swoop asks, doors crossed, terminals used, walks searched.</summary>
    public (long Mounts, long Doors, long Used, long Paths) Errands()
    {
        (long Mounts, long Doors, long Used, long Paths) sum = default;
        foreach (var bot in Bots)
        {
            if (bot != null)
            {
                var e = bot.Errands();
                sum = (sum.Mounts + e.Mounts, sum.Doors + e.Doors, sum.Used + e.Used, sum.Paths + e.Paths);
            }
        }

        return sum;
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

    /// <summary>
    /// Starts the driver and the reconciler, then waits for the population to first reach <see cref="Target"/>.
    /// </summary>
    /// <param name="ct">Cancels the wait, not the swarm.</param>
    /// <returns>How many sessions are open when the target is first reached, or when opening stops making progress.</returns>
    /// <remarks>
    /// <para>
    /// <b>The driver starts BEFORE the ramp, and that ordering is a fix.</b> It used to start after the last session opened, so during
    /// a ramp nothing walked and nothing pinged except a keepalive sweep that existed only to cover the gap. At a thousand sessions the
    /// ramp was seconds and nobody noticed; at ten thousand the ramp was minutes, so an operator watched a filling world in which not
    /// one avatar moved, and the sweep — which pings the WHOLE population on a cadence — grew with the fleet until it starved the
    /// connects it was interleaved with. The ramp went from 400 sessions a second to about one. Driving from the first batch makes the
    /// sweep redundant: the driver pings everything it finds, including the sessions that arrived a moment ago.
    /// </para>
    /// <para>
    /// <b>And the population is now a TARGET rather than a count.</b> <see cref="Target"/> can be moved at any time, up to the
    /// capacity the swarm was built with, and the reconciler opens or closes sessions to match it at the connect rate — which is what
    /// makes "turn it up to five thousand and watch what happens" an action rather than a restart.
    /// </para>
    /// </remarks>
    public async Task<int> StartAsync(CancellationToken ct = default)
    {
        // <b>Its OWN loop, not a pass inside the driver's tick, and the difference is thousands of sessions.</b> The server closes a
        // session it has not heard a PING from within 3 / pingHz seconds — 750 ms at the 4 Hz this one advertises — so what matters
        // is the INTERVAL between two pings to the same bot. Inside the drive tick that interval is the ping pass plus the movement
        // pass plus the timer's wait, which at a few thousand sessions crosses the window exactly while the ramp is also opening
        // sockets: measured, the server closed 880 of 3 000 for silence and the client could only see them as 1006. On its own loop
        // the interval is bounded by the ping pass alone, whatever the rest of the fleet is doing.
        if (_options.Brain && _options.Kind == "player" && BotProvisioner.AuthBaseFrom(_options.Endpoint) is { } server)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                _assets = await BotAssets.LoadAsync(http, server, ct).ConfigureAwait(false);
                Console.WriteLine(_assets == null
                    ? "  brain: the server publishes no asset set; bots walk straight lines and enter no building"
                    : $"  brain: {_assets.Count} structure assets read; bots walk round buildings, through doors and to terminals");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException
                or InvalidOperationException or FormatException)
            {
                Console.WriteLine($"  !! brain: the asset set could not be read ({e.Message}); bots walk straight lines and enter no building");
            }
        }

        _pinger = Task.Run(() => PingLoopAsync(_stopping.Token), CancellationToken.None);
        _driver = Task.Run(() => DriveAsync(_stopping.Token), CancellationToken.None);
        _reconciler = Task.Run(() => ReconcileAsync(_stopping.Token), CancellationToken.None);

        // The server's own close reasons, polled rather than inferred from close codes. See PollServerStatsAsync.
        _statsPoller = Task.Run(() => PollServerStatsAsync(_stopping.Token), CancellationToken.None);

        await _settled.Task.WaitAsync(ct).ConfigureAwait(false);
        return BotCount;
    }

    /// <summary>Opens and closes sessions until the live population matches <see cref="Target"/>, for as long as the swarm lives.</summary>
    /// <param name="ct">Stops the reconciler.</param>
    private async Task ReconcileAsync(CancellationToken ct)
    {
        // <b>Guarded, because a throw in here used to disappear and take the dial with it.</b> Append invokes OnResumeToken, which
        // writes the accounts file: an IOException from a locked file or a full disk faulted this task, left _settled uncompleted so
        // StartAsync waited on it until Stop, and reported nothing anywhere. After settling it was worse — the reconciler died
        // silently, the panel still said Running, and moving the target did nothing at all.
        try
        {
            await ReconcileLoopAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception e)
        {
            Interlocked.CompareExchange(ref _firstOpenFailure, "the ramp stopped: " + e.Message, null);
            Console.WriteLine($"  !! the fleet's reconciler stopped: {e.Message}");
        }
        finally
        {
            // So StartAsync cannot wait forever on a loop that is no longer running.
            _settled.TrySetResult();
        }
    }

    /// <summary>The reconciler's loop proper; <see cref="ReconcileAsync"/> owns its failure handling.</summary>
    /// <param name="ct">Stops the loop.</param>
    private async Task ReconcileLoopAsync(CancellationToken ct)
    {
        // <b>The chunk is the pipeline's unit of work and --connect-batch no longer sets it.</b> That knob means `rate x stagger` — 8
        // at 400/s — which is a sensible quantum for a socket and a disastrous one for a batch of signups: it made a 10 000 ramp into
        // 1 250 provisioning passes, each one rewriting and fsyncing the whole accounts file. This document's own design says the
        // chunk is deliberately not derived from that product, and preferring ConnectBatch here contradicted it.
        //
        // The pacing below still honours --connect-batch, which is what existing command lines actually ask of it: how many sockets
        // to open between two staggers.
        var chunk = Math.Max(1, _options.ChunkSize);

        // The deadline a chunk is paced to. A chunk of 250 at 400/s is 625 ms; with an explicit --connect-batch the stagger is that
        // batch's share of the rate, scaled up to the chunk, so the achieved rate still matches the number asked for.
        var perBatch = _options.ConnectRatePerSecond > 0
            ? TimeSpan.FromSeconds((double)chunk / _options.ConnectRatePerSecond)
            : TimeSpan.FromTicks(_options.ConnectStagger.Ticks * Math.Max(1, chunk / Math.Max(1, _options.ConnectBatch)));

        // PACED AGAINST A DEADLINE, not slept for after the fact. Delaying a fixed stagger after awaiting the batch makes the period
        // `handshake + stagger` rather than `max(handshake, stagger)`, so the achieved rate was `size / (stagger + RTT)`: on the 60 ms
        // link this tool exists to measure over, `--connect-rate 200` delivered about fifty a second while reporting two hundred. For a
        // load generator whose report IS the product, a knob that overstates by four times is worse than no knob.
        var clock = Stopwatch.StartNew();
        var nextDue = TimeSpan.Zero;
        var stalls = 0;

        // <b>Rounds that replaced sessions without gaining any, which is a different failure from a round that opened nothing.</b>
        // Retirement turns a permanent loss into a retry, and a retry against a condition that is not going to change turns into a
        // hot loop: measured with the server's hot spinners disabled, every handshake died on arrival and the fleet replaced 21 665
        // sessions in five minutes — about seventy a second — while the population sat at 154 and the panel reported "Running".
        // The per-chunk stall counter cannot see it, because each chunk DOES open sessions; what it does not do is keep them.
        var churnRounds = 0;
        var churnReported = false;

        while (!ct.IsCancellationRequested)
        {
            var target = Math.Clamp(Volatile.Read(ref _target), 0, _bots.Length);
            var liveBefore = BotCount;

            // <b>Reality before intent, every round.</b> Without this the population was "slots ever filled": a session the SERVER
            // closed stayed in its slot and in the count, so `live` counted corpses, compared them against the target, and decided
            // there was nothing to do. One lost session was permanent. Measured on the control panel before this line existed:
            // 8 000 opened, 4 407 closed by the server for silence, the dial stuck at 3 593 for ten minutes while the panel still
            // said it was aiming for 8 000 — and growth never ran again because 7 997 of 8 000 slots were full of closed sockets.
            await RetireDisconnectedAsync().ConfigureAwait(false);
            var live = BotCount;

            if (live < target)
            {
                var size = Math.Min(chunk, target - live);

                // <b>A churning fleet probes rather than charges.</b> Backing the CADENCE off to one round every two seconds still
                // threw 250 handshakes at a server that was closing all of them — 44 a second, measured, against 72 with no backoff
                // at all, which is not a fix so much as a discount. A chunk of this size is enough to notice the moment the
                // condition clears, and the round that gains ground clears the churn counter and restores the full chunk.
                if (churnRounds >= 3)
                {
                    size = Math.Min(size, ChurnProbe);
                }

                // <b>Accounts for THIS chunk, here, rather than for the whole population before the loop starts.</b> That is the
                // pipeline: these are signing up while the chunk before them is already walking. A chunk whose accounts are already
                // good makes no HTTP calls at all, which is what makes a shrink followed by a re-grow free.
                // Timed, because provisioning and connecting are ONE step now and the slower half decides the achieved rate. Without
                // this, --connect-rate would report what was asked for while the signups bound it — the same overstatement the
                // deadline-paced connect loop was written to avoid, and a knob that overstates is worse than no knob.
                // <b>Account indices come from their own allocator, never from the array slot.</b> `live` is a COMPACTED count — a
                // failed handshake leaves it short — so using it as the account index made one failure shift the whole window
                // backwards: the next chunk re-opened an account that was already playing, the server kicked the arriving session
                // for it (AccountAlreadyPlaying), and the gap was never retried because the count had not moved.
                var first = AllocateIndices(size, out var allocated);
                if (allocated <= 0)
                {
                    // <b>A stall, NOT an exit.</b> `break` here left the whole reconciler loop, so the dial stopped working in both
                    // directions for the rest of the run while the panel still said Running — exactly what this method's remarks
                    // promise cannot happen. Settling after three such rounds is what the caller is waiting on; staying in the loop
                    // is what keeps a LOWER target still reachable, which matters most when the fleet is out of indices.
                    stalls++;
                    if (stalls >= 3)
                    {
                        _settled.TrySetResult();
                    }

                    nextDue = clock.Elapsed;
                    await Task.Delay(StalledPoll, ct).ConfigureAwait(false);
                    continue;
                }

                var chunkClock = Stopwatch.StartNew();
                var provisioned = await EnsureChunkAsync(first, allocated, ct).ConfigureAwait(false);
                var provisionMs = chunkClock.Elapsed.TotalMilliseconds;
                if (provisioned <= 0)
                {
                    // Handed back, or a chunk whose signups failed would burn its indices and the fleet would walk up to its
                    // capacity without ever opening a session.
                    ReleaseIndices(first, allocated);

                    // Treated exactly like a chunk that opened nothing: the stall counter below decides whether to give up, so a
                    // server refusing signups cannot leave StartAsync waiting forever.
                    stalls++;
                    if (stalls >= 3)
                    {
                        _settled.TrySetResult();
                    }

                    nextDue = clock.Elapsed;
                    await Task.Delay(StalledPoll, ct).ConfigureAwait(false);
                    continue;
                }

                // <b>The WHOLE chunk is opened, not the provisioned count.</b> The hook answers how many accounts are usable, not
                // WHICH, so opening only the first `provisioned` of them was right just when the failures happened to be the last
                // entries: with one in the middle it opened the failed index and skipped a good one at the end. A bot whose token is
                // missing instead fails its handshake and is counted with its real reason, and its index goes back to the allocator.
                var opening = new Task<Bot>[allocated];
                for (var k = 0; k < allocated; k++)
                {
                    opening[k] = OpenOneAsync(first + k, ct);
                }

                chunkClock.Restart();
                var opened = await Task.WhenAll(opening).ConfigureAwait(false);
                var added = Append(opened);
                NoteChunkCost(provisionMs, chunkClock.Elapsed.TotalMilliseconds);

                // The indices whose handshake failed go back, so the next chunk retries exactly those rather than allocating fresh
                // ones and leaving a hole no session ever fills.
                for (var k = 0; k < allocated; k++)
                {
                    if (opened[k] == null)
                    {
                        ReleaseIndices(first + k, 1);
                    }
                }

                // A ramp that stops making progress is settled too, or StartAsync would wait forever for sessions the server is
                // refusing — which is exactly what a full session table does.
                stalls = added > 0 ? 0 : stalls + 1;

                // <b>NET progress, not sessions opened.</b> A round that opens 250 and loses 250 has opened a chunk and gained
                // nothing, and at the connect pacing it will do it again in a second and a half, forever. Three such rounds and the
                // ramp drops to the stalled poll: the fleet still replaces what it loses, at a rate an operator can watch, and the
                // dial still answers a lower target. The alternative measured itself: seventy handshakes a second against a server
                // that closed every one of them.
                churnRounds = BotCount > liveBefore ? 0 : churnRounds + 1;
                if (churnRounds >= 3 && !churnReported)
                {
                    churnReported = true;
                    var note = $"sessions are closing as fast as they open — {RetiredDead:N0} replaced so far, population {BotCount:N0} "
                        + $"of {target:N0}. Backing off to one chunk every {StalledPoll.TotalSeconds:F0}s. Read 'Closed by the server' "
                        + "and the disconnect codes: this is the server refusing or dropping them, not the ramp being slow.";
                    _options.OnNote?.Invoke(note);
                    Console.WriteLine($"  !! {note}");
                }
                else if (churnRounds == 0)
                {
                    churnReported = false;
                }

                if (added > 0 || stalls < 3)
                {
                    if (churnRounds >= 3)
                    {
                        // Re-based, because the slow poll owns the cadence while the fleet is churning; the connect deadline is
                        // picked up again by the round that finally gains ground.
                        nextDue = clock.Elapsed;
                        await Task.Delay(StalledPoll, ct).ConfigureAwait(false);
                        continue;
                    }

                    nextDue += perBatch;
                    var remaining = nextDue - clock.Elapsed;
                    if (remaining > TimeSpan.Zero)
                    {
                        await Task.Delay(remaining, ct).ConfigureAwait(false);
                    }

                    continue;
                }

                _settled.TrySetResult();
            }
            else if (live > target)
            {
                await ShrinkToAsync(target, chunk).ConfigureAwait(false);
            }
            else
            {
                // Reset here, or a ramp that stalled once would leave the reconciler on its slow poll for the rest of the run and a
                // later growth would start a couple of seconds late for no reason.
                stalls = 0;
                _settled.TrySetResult();
            }

            // Idle or settled: the deadline is re-based so a later growth is not charged for the time spent waiting.
            //
            // <b>And a stalled ramp backs off.</b> A target the server will not grant — a full session table, accounts it refuses —
            // otherwise retried ten times a second forever, which turns a refusal into a load test of its own. The slow poll still
            // notices when the operator lowers the target or the server recovers.
            nextDue = clock.Elapsed;
            await Task.Delay(stalls >= 3 ? StalledPoll : IdlePoll, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Takes every session the server has closed out of the population, so the reconciler can re-open it.</summary>
    /// <returns>How many were removed.</returns>
    /// <remarks>
    /// <para>
    /// <b>This is the fleet's one closed loop.</b> Everything else here states an intention — a target, a connect rate, a chunk —
    /// and a load generator that only ever counts what it opened cannot tell "eight thousand sessions" from "eight thousand
    /// handshakes, half of them now closed". A bot is alive if and only if its socket is open, and that is read from the socket
    /// rather than remembered.
    /// </para>
    /// <para>
    /// <b>Under the append lock, with the count lowered before the slots are emptied</b>, which is the ordering the shrink already
    /// uses and for the same reason: the driver and the ping loop read the count and then the slot, so a bot has to become
    /// unreachable before its socket is disposed. The arithmetic is <see cref="BotRoster.Compact"/>'s, where it has tests.
    /// </para>
    /// <para>
    /// <b>The index goes back to the pool, which is what lets the next chunk re-open the SAME account.</b> A dead bot whose index
    /// leaked would cost the fleet its reach permanently — the failure <see cref="BotIndexPool"/> was extracted over.
    /// </para>
    /// </remarks>
    private async Task<int> RetireDisconnectedAsync()
    {
        List<Bot> victims;

        lock (_appendLock)
        {
            var live = _botCount;
            if (live == 0)
            {
                return 0;
            }

            victims = [];
            var kept = BotRoster.Compact(_bots, live, static bot => bot.Client.IsConnected, victims);
            if (victims.Count == 0)
            {
                return 0;
            }

            Volatile.Write(ref _botCount, kept);
        }

        // <b>Disposed, not closed.</b> The server has already closed these: a CloseAsync would wait on a handshake that cannot
        // arrive, and a shrink's reason for sending one — so the server does not hold the avatar for the whole resume grace — does
        // not apply, because the server is the party that ended it. Concurrently, since a few thousand can die at once.
        var releasing = new Task[victims.Count];
        for (var i = 0; i < victims.Count; i++)
        {
            releasing[i] = DisposeQuietlyAsync(victims[i]);
        }

        await Task.WhenAll(releasing).ConfigureAwait(false);

        // After the dispose, so an index cannot be re-opened while the socket it names is still being torn down.
        foreach (var bot in victims)
        {
            ReleaseIndices(bot.Index, 1);
        }

        Interlocked.Add(ref _retiredDead, victims.Count);
        return victims.Count;
    }

    /// <summary>Disposes one dead bot's client, swallowing whatever a socket that is already gone says on the way out.</summary>
    /// <param name="bot">The bot.</param>
    /// <returns>The dispose.</returns>
    private static async Task DisposeQuietlyAsync(Bot bot)
    {
        try
        {
            await bot.Client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A retirement pass must not fail because one socket was further gone than another.
        }
    }

    /// <summary>Reads the server's close reasons and session count, every two seconds, for as long as the swarm lives.</summary>
    /// <param name="ct">Stops the poll.</param>
    /// <returns>The loop.</returns>
    /// <remarks>
    /// <b>Failures are recorded, not thrown.</b> A fleet whose stats poll cannot reach the server must still run — the poll is an
    /// instrument, not a dependency — so the error is published for the panel to show instead of taking the swarm down with it.
    /// </remarks>
    private async Task PollServerStatsAsync(CancellationToken ct)
    {
        Uri statsUri;
        try
        {
            statsUri = new Uri(BotProvisioner.AuthBaseFrom(_options.Endpoint), "typhon/stats.json");
        }
        catch (UriFormatException e)
        {
            lock (_reportLock)
            {
                _serverStatsError = e.Message;
            }

            return;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));

        while (await SafeWaitAsync(timer, ct).ConfigureAwait(false))
        {
            try
            {
                var json = await http.GetStringAsync(statsUri, ct).ConfigureAwait(false);
                using var document = JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty("replication", out var replication))
                {
                    continue;
                }

                var silent = replication.TryGetProperty("closedSilent", out var a) ? a.GetInt64() : 0L;
                var lagging = replication.TryGetProperty("closedLagging", out var b) ? b.GetInt64() : 0L;
                var sessions = replication.TryGetProperty("sessions", out var c) ? c.GetInt64() : 0L;

                lock (_reportLock)
                {
                    _serverClosedSilent = silent;
                    _serverClosedLagging = lagging;
                    _serverSessionsSeen = sessions;
                    _serverStatsError = null;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (e is HttpRequestException or JsonException)
            {
                lock (_reportLock)
                {
                    _serverStatsError = e.Message;
                }
            }
        }
    }

    /// <summary>Takes a contiguous run of account indices for a chunk.</summary>
    /// <param name="want">How many the chunk would like.</param>
    /// <param name="allocated">How many it got; zero means the pool is exhausted.</param>
    /// <returns>The first index of the run.</returns>
    /// <remarks>
    /// The arithmetic and its invariants live in <see cref="BotIndexPool"/>, with tests, because handing out an index that is still
    /// in use logs a second session into a live account and the server kicks it.
    /// </remarks>
    private int AllocateIndices(int want, out int allocated) => _indices.Allocate(want, out allocated);

    /// <summary>Gives account indices back, one or many.</summary>
    /// <param name="from">The first index.</param>
    /// <param name="count">How many.</param>
    private void ReleaseIndices(int from, int count) => _indices.Release(from, count);

    /// <summary>Records what a chunk spent on each half of the pipeline, and which half bound it.</summary>
    /// <param name="provisionMs">Milliseconds signing the chunk's accounts up.</param>
    /// <param name="connectMs">Milliseconds opening its sessions.</param>
    private void NoteChunkCost(double provisionMs, double connectMs)
    {
        lock (_reportLock)
        {
            _chunks++;
            _provisionMsTotal += provisionMs;
            _connectMsTotal += connectMs;
            if (provisionMs > connectMs)
            {
                _chunksBoundByProvisioning++;
            }
        }
    }

    /// <summary>
    /// What the ramp spent, and which half of the pipeline bound it.
    /// </summary>
    /// <returns>Chunks, mean milliseconds per half, and how many chunks the signups bound.</returns>
    /// <remarks>
    /// <b>The report has to say this, because the two halves are now one step.</b> Signups run on the SERVER's cores behind a
    /// PBKDF2; connects run on its tick. If the signups are the slower half then --connect-rate describes an intention rather than
    /// an outcome, and a load generator whose report is its product must not overstate what it delivered.
    /// </remarks>
    public (int Chunks, double MeanProvisionMs, double MeanConnectMs, int BoundByProvisioning) ChunkCost()
    {
        lock (_reportLock)
        {
            return _chunks == 0
                ? (0, 0, 0, 0)
                : (_chunks, _provisionMsTotal / _chunks, _connectMsTotal / _chunks, _chunksBoundByProvisioning);
        }
    }

    /// <summary>Provisions a chunk's accounts, answering how many of its bots can now be opened.</summary>
    /// <param name="from">The first bot index in the chunk.</param>
    /// <param name="count">How many bots the chunk wanted.</param>
    /// <param name="ct">Cancels the pass.</param>
    /// <returns>How many are usable: the whole chunk when there is nothing to provision.</returns>
    private async Task<int> EnsureChunkAsync(int from, int count, CancellationToken ct)
    {
        var ensure = _options.EnsureAccounts;
        if (ensure == null)
        {
            return count;
        }

        try
        {
            var ready = await ensure(from, count, ct).ConfigureAwait(false);
            return Math.Clamp(ready, 0, count);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception e)
        {
            // Reported once and counted, not thrown: a chunk that cannot sign up must stall the ramp rather than tear the swarm
            // down, and the reason has to reach the operator — a refusal that is swallowed looks exactly like a generator that
            // stopped trying, which is the mistake this whole area has already made twice.
            Interlocked.Increment(ref _openFailures);
            if (Interlocked.CompareExchange(ref _firstOpenFailure, e.Message, null) == null)
            {
                Console.WriteLine($"  !! a chunk's accounts could not be provisioned: {e.Message}");
            }

            return 0;
        }
    }

    /// <summary>Counts how many of a chunk's handshakes produced a session.</summary>
    /// <remarks>
    /// <b>It no longer publishes anything, and the name is kept because its one caller asks exactly this.</b> Publication moved into
    /// <see cref="AppendOne"/>, called from each handshake, so by the time a chunk's <c>WhenAll</c> completes every bot in it is
    /// already in the population — see <see cref="OpenOneAsync"/> for why that timing matters. What the reconciler still needs from
    /// here is whether the chunk made progress, and a null entry is a handshake that failed.
    /// </remarks>
    private int Append(Bot[] opened)
    {
        var added = 0;
        foreach (var bot in opened)
        {
            if (bot != null)
            {
                added++;
            }
        }

        return added;
    }

    /// <summary>Publishes one freshly opened bot to the population, so the ping loop and the driver can both see it.</summary>
    /// <param name="bot">The bot, already past its handshake.</param>
    /// <remarks>
    /// <b>Called from each handshake rather than once per chunk</b> — see <see cref="OpenOneAsync"/> for why that timing is the
    /// difference between keeping a population and losing it. Several handshakes finish at once, so the slot is taken under the
    /// swarm's lock; the count is published with a release write afterwards, which is the ordering every reader of
    /// <see cref="Bots"/> already relies on.
    /// </remarks>
    private bool AppendOne(Bot bot)
    {
        bot.WatchRealm();

        // After the handshake, because the token arrives in WELCOME: asking before the connect would persist the token the LAST
        // run was given, which is the one that has just been spent.
        _options.OnResumeToken?.Invoke(bot.Index, bot.Client.ResumeToken);

        lock (_appendLock)
        {
            var at = _botCount;
            if (at >= _bots.Length)
            {
                // <b>Reported, not swallowed.</b> The allocator should make this unreachable, but a session that is open and in no
                // slot is the worst of both: the server holds an avatar for it while nothing in this process ever pings it, so it
                // dies of silence a second later and the fleet counts it as a loss it cannot explain. The caller closes it instead.
                return false;
            }

            _bots[at] = bot;
            Volatile.Write(ref _botCount, at + 1);
            return true;
        }
    }

    /// <summary>Closes sessions from the tail until the population is down to <paramref name="target"/>.</summary>
    /// <param name="target">The population to come down to.</param>
    /// <param name="batch">How many to close at once, so a big cut does not arrive as one burst of closes.</param>
    /// <remarks>
    /// <b>From the tail, and the count is lowered BEFORE the socket is closed.</b> The driver reads the count and then the slot, so
    /// retiring a bot the driver may still be mid-tick on has to make it unreachable first — otherwise the driver sends on a client
    /// that is being disposed, which surfaces as a protocol fault blamed on the catalog.
    /// <para>
    /// Closed properly rather than dropped, which is the difference between a shrink and a crash: a dropped socket leaves the server
    /// holding the avatar for its whole resume grace, so cutting a fleet in half would leave half a population standing for a minute
    /// and the next measurement would include them.
    /// </para>
    /// </remarks>
    private async Task ShrinkToAsync(int target, int batch)
    {
        Bot[] going;

        // <b>The count, the victims and the count's new value, all under one lock.</b> Reading `live` outside it and taking the
        // lock only for the write left a window an append could land in: a handshake publishing at the old count would be stranded
        // ABOVE the lowered one, where nothing pings it, drives it, closes it or releases its index — it would die of silence and be
        // counted as a loss nobody could explain. The single-threaded reconciler happens to make that unreachable today, because it
        // awaits every handshake in the chunk before it can shrink; that is the await's guarantee, not this lock's, and a comment
        // claiming the lock gives it would be the kind of false assurance the next change trips over.
        lock (_appendLock)
        {
            var live = BotCount;
            var cut = Math.Min(batch, live - target);
            if (cut <= 0)
            {
                return;
            }

            going = new Bot[cut];
            for (var i = 0; i < cut; i++)
            {
                going[i] = _bots[live - 1 - i];
            }

            Volatile.Write(ref _botCount, live - cut);
            for (var i = 0; i < cut; i++)
            {
                _bots[live - 1 - i] = null;
            }
        }

        // <b>The count dropped first so no NEW reader reaches these slots; every reader must still tolerate a null.</b> Ordering
        // alone cannot make this safe in either direction — a reader that captured the old count is walking slots the block above
        // has emptied whichever order they are written in — so the guarantee is the readers', and `Bots` is documented accordingly.
        // Getting this wrong put a NullReferenceException through the control panel's snapshot and killed the Blazor circuit, which
        // is the failure the array-and-count design was adopted to prevent in the first place.
        var removing = going.Length;
        var closing = new Task[removing];
        for (var i = 0; i < removing; i++)
        {
            closing[i] = CloseOneAsync(going[i]);
        }

        await Task.WhenAll(closing).ConfigureAwait(false);
        Interlocked.Add(ref _retired, removing);

        // <b>ONE AT A TIME, because the retired slots are not a contiguous run of account indices.</b> This used to release
        // `(min(index), removing)` as a single block, which was right only while slot order matched index order — true when a whole
        // chunk was published together, false now that each handshake publishes itself in whatever order it completes. Releasing a
        // block that the retired set merely spans marked live indices free: the next growth re-opened an account still playing and
        // the server refused the arriving session, which is the one failure the allocator exists to prevent. The free list
        // coalesces, so these come back together as one run anyway.
        foreach (var bot in going)
        {
            ReleaseIndices(bot.Index, 1);
        }
    }

    /// <summary>Closes one bot's session, swallowing whatever a dying socket says on the way out.</summary>
    /// <param name="bot">The bot.</param>
    private static async Task CloseOneAsync(Bot bot)
    {
        try
        {
            // <b>CloseAsync before DisposeAsync, and the first one is the whole point.</b> Dispose only drops the socket: no close
            // frame leaves, so the server classifies the session as link-dead and holds its avatar for the entire resume grace. A
            // shrink from 5 000 to 1 000 would then report 4 000 retired while the server kept 4 000 bodies standing, replicated and
            // in the spatial index, for a minute — exactly what this method's own remarks promise cannot happen.
            await bot.Client.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A socket that is already gone cannot be told goodbye; the dispose below is still owed.
        }

        try
        {
            await bot.Client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A shrink must not fail because one socket was already gone.
        }
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
        var bot = new Bot(index, _options, _assets);
        bot.Client.Disconnected += (code, _) => Note(code, bot.Client.CloseDetail);

        // Interlocked, because Fault is raised from each client's OWN receive loop: with N bots these are N threads, and `Faults++` is a
        // read-modify-write that silently loses updates. The report is the product here, so a counter that undercounts is the report lying.
        bot.Client.Fault += _ => Interlocked.Increment(ref _faults);

        try
        {
            await bot.Client.ConnectAsync(ct).ConfigureAwait(false);

            // <b>Published HERE, not after the chunk's WhenAll, and this one line is worth thousands of sessions.</b> The server
            // starts a session's silence clock when it binds it at HELLO and closes it if no PING arrives within 3 / pingHz
            // seconds. Appending a whole chunk at once meant the FIRST bot to finish its handshake waited for the slowest of its
            // 249 neighbours before it was visible to the ping loop at all -- a mean of 456 ms and a tail well past the 750 ms
            // window, so the earliest openers in every chunk were killed for silence before the fleet had ever pinged them. That
            // is what cost a one-process fleet 7 023 sessions down to 4 314 when the ramp's own ping sweep was removed: the sweep
            // was covering exactly this gap.
            if (!AppendOne(bot))
            {
                await bot.Client.CloseAsync().ConfigureAwait(false);
                await bot.Client.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            return bot;
        }
        catch (OperationCanceledException)
        {
            // <b>Not counted, and that is the point.</b> Pressing Stop mid-ramp cancels every connect in flight; counting those as
            // refusals made a clean shutdown print "N session(s) were refused or failed to open. First reason: The operation was
            // canceled", which is a shutdown reported as a server problem.
            await bot.Client.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch (Exception e)
        {
            // A handshake that failed is a disconnect with no code: counted as zero so a run that could not open its population is visible in the same
            // table as one that lost it later.
            Note(0);

            // <b>And the REASON, which used to be discarded here.</b> A fleet that asked for 10 000 and opened 8 192 reported only the two numbers, so a
            // server refusing for a full session table looked exactly like a generator that stopped trying — and the close reason the server does send
            // ("the session table is full (8192)") was thrown away one line above the counter that said something had gone wrong. The first message is
            // kept rather than every one: ten thousand copies of one sentence is not a diagnostic.
            Interlocked.Increment(ref _openFailures);
            if (Interlocked.CompareExchange(ref _firstOpenFailure, e.Message, null) == null)
            {
                Console.WriteLine($"  !! a session could not be opened: {e.Message}");
            }

            await bot.Client.DisposeAsync().ConfigureAwait(false);
            return null;
        }
    }

    // SweepRampPingsAsync lived here and is gone with the ramp it served. It pinged the WHOLE population on a cadence, interleaved
    // with the connects, purely because the driver did not start until the last session was open — so it was O(population) sends
    // competing with the opens, and by six thousand sessions it had slowed the ramp from 400 a second to about one. The driver now
    // starts first and pings everything it finds, including a bot that arrived a moment ago, so there is nothing left for a sweep to
    // cover. Deleted rather than left unreferenced: a second pinger is a second thing to reason about on the next slow ramp.

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);

        // Both, and the reconciler first: it is the one that opens sockets, so letting it run while the bots are being closed would
        // race a new session into an array the teardown has already walked.
        foreach (var task in new[] { _reconciler, _driver, _pinger, _statsPoller })
        {
            if (task == null)
            {
                continue;
            }

            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Shutting down; neither loop has anywhere to report a failure on the way out.
            }
        }

        // Indexed, not a span: a ref struct cannot live across an await.
        // Null-checked, because a shrink leaves the slots above the count empty and a teardown racing one would dereference the hole
        // it just made.
        var live = BotCount;
        for (var i = 0; i < live; i++)
        {
            var bot = _bots[i];
            if (bot != null)
            {
                await CloseOneAsync(bot).ConfigureAwait(false);
            }
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
    private void Note(ushort code, string detail = null)
    {
        lock (_reportLock)
        {
            _disconnects[code] = _disconnects.GetValueOrDefault(code) + 1;

            // <b>The local failure behind an abrupt close, counted by kind.</b> 1006 means only that no close frame arrived, so a
            // table of three thousand of them says nothing about why — and a reset by the peer, a local abort and a timeout are
            // three different investigations. Counted rather than printed: one line per session would be the log nobody reads.
            if (detail != null)
            {
                _closeDetails[detail] = _closeDetails.GetValueOrDefault(detail) + 1;
            }
        }
    }

    /// <summary>Abnormal closes by the local failure that revealed them, which the close code cannot express.</summary>
    /// <returns>A snapshot, commonest first.</returns>
    public IReadOnlyList<(string Detail, int Count)> CloseDetails()
    {
        lock (_reportLock)
        {
            var list = new List<(string, int)>(_closeDetails.Count);
            foreach (var pair in _closeDetails)
            {
                list.Add((pair.Key, pair.Value));
            }

            list.Sort((a, b) => b.Item2.CompareTo(a.Item2));
            return list;
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

            // Players only: MoveDir is Roles(Player), so a god camera asking to walk would be refused a command it has no body for.
            // The brain replaces wandering rather than joining it: two things steering one body would fight over the heading.
            var thinking = _options.Brain && _options.Kind == "player";
            var wandering = !thinking && _options.WanderSeconds > 0 && _options.Kind == "player";

            // Indexed for the same reason DisposeAsync is: the body awaits.
            var live = BotCount;

            // The pings are NOT here: PingLoopAsync owns them on its own cadence. See StartAsync.
            var driveStart = Stopwatch.GetTimestamp();

            // <b>CONCURRENTLY within a batch, which is the same fix StartAsync's ramp already carries.</b> Awaited one bot at a time —
            // as this loop did — a tick costs N send round trips in series, so the driver's real ceiling is one bot per round trip
            // whatever the timer says. It held at a thousand and fell apart at ten: a 90-second run managed 58 of its 360 ticks, 0.64 Hz
            // against the 4 Hz asked for, and the sessions it then stopped pinging were closed by the server for silence — 9 805 of them.
            // That read as a server limit, a socket limit and a memory limit in turn, and it was none of those.
            //
            // Bounded rather than one WhenAll over every bot: ten thousand simultaneous sends each hold a buffer, and the burst is the
            // thing the stagger exists to avoid. A batch overlaps its own latency while keeping the in-flight count predictable.
            for (var b = 0; b < live; b += DriveBatch)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                var size = Math.Min(DriveBatch, live - b);
                var driving = new Task[size];
                for (var k = 0; k < size; k++)
                {
                    // b + k + 1 preserves the old one-based position, which the tour's stagger is phased on.
                    driving[k] = DriveOneAsync(_bots[b + k], b + k + 1, touring, tourPeriod, moveRegions, wandering, thinking, ct);
                }

                await Task.WhenAll(driving).ConfigureAwait(false);
            }

            NoteDriveSweep(Stopwatch.GetElapsedTime(driveStart).TotalMilliseconds);
        }
    }

    /// <summary>Pings every live session on its own cadence, independently of the driver.</summary>
    /// <param name="ct">Stops the loop.</param>
    /// <returns>The loop.</returns>
    /// <remarks>
    /// <para>
    /// <b>The one loop whose latency decides whether the fleet stays connected.</b> It does nothing but ping: no movement, no
    /// region, no catalog lookups, nothing that can be slow. See <see cref="StartAsync"/> for the measurement that moved it here.
    /// </para>
    /// <para>
    /// At the drive rate and NOT above it, which was measured rather than assumed: at twice the rate the sweep itself grew from a
    /// 346 ms worst case to 604 ms and retention did not move, because this process is CPU-bound well before it is cadence-bound.
    /// Decoupling the interval from the driver's SCHEDULE is the part that is worth having; paying twice for it is not.
    /// </para>
    /// <para>
    /// <b>Decoupled from the driver's schedule, NOT from its sends, and the difference bounds what this loop can promise.</b> Both
    /// paths send through one <see cref="TyphonClient"/>, and the transport serialises them behind a single send lock — which is
    /// what makes concurrent sends safe, and equally what makes them queue. A bot whose kernel send buffer has filled blocks its own
    /// ping behind whatever the driver is sending to it. What this loop guarantees is that the stall stays that bot's: a ping is
    /// issued and not awaited, so a stuck socket costs one permit of the population's worth and never another bot's cadence. It
    /// used to await a batch of 4 096, which is how one stalled socket took a batch's worth of sessions with it.
    /// </para>
    /// </remarks>
    private async Task PingLoopAsync(CancellationToken ct)
    {
        var hz = Math.Max(1, _options.TickHz);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / hz));

        // <b>Backpressure instead of a barrier, and one permit per POSSIBLE bot.</b> The permit is released when a ping completes, so
        // a stalled socket costs one permit and not the rest of the sweep. Capping the permits below the population brings the
        // barrier back in another shape: measured at 8 000 with a cap of 4 096, the sweep took 432 ms merely to ISSUE and its worst
        // was 786 ms — past the silence window again — because the second half of the fleet was waiting on the first half's
        // completions. A ping is a few bytes with no encode, so the population is the only bound it needs; the burst this used to
        // guard against is the driver's, and DriveBatch still bounds that.
        var inFlight = Math.Max(1, _bots.Length);
        using var permits = new SemaphoreSlim(inFlight, inFlight);

        while (await SafeWaitAsync(timer, ct).ConfigureAwait(false))
        {
            var live = BotCount;
            var start = Stopwatch.GetTimestamp();
            for (var b = 0; b < live; b++)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    await permits.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                // Not awaited: every exception is contained inside, and the permit is released in its finally. Awaiting here — even
                // a batch at a time, as this loop used to — makes the slowest socket in the batch the sweep's own latency, and a
                // sweep slower than the server's silence window costs the whole batch its session. Measured on the control panel at
                // 8 000: worst sweep 932 ms against a 750 ms window, and the server closed 927 sessions for silence in one burst.
                _ = PingThenReleaseAsync(_bots[b], permits, ct);
            }

            // <b>This is now the time to ISSUE a sweep, not to complete one</b>, which is the number that matters: a ping's own cost
            // is PingCost's, and what decides whether a session lives is how long it waits to be sent at all.
            NotePingSweep(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
    }

    /// <summary>Pings one bot and releases its permit, whatever happened.</summary>
    /// <param name="bot">The bot, which may be null if a retirement emptied its slot.</param>
    /// <param name="permits">The in-flight bound to release.</param>
    /// <param name="ct">Cancels the send.</param>
    /// <returns>The ping.</returns>
    /// <remarks>
    /// <b>Nothing may escape this method</b>, because the sweep does not await it: an exception here would be an unobserved task
    /// fault, and a permit never released would shrink the fleet's ping capacity by one for the rest of the run.
    /// <see cref="PingOneAsync"/> already contains every exception; the finally is what makes the permit safe regardless.
    /// </remarks>
    private async Task PingThenReleaseAsync(Bot bot, SemaphoreSlim permits, CancellationToken ct)
    {
        try
        {
            await PingOneAsync(bot, ct).ConfigureAwait(false);
        }
        finally
        {
            permits.Release();
        }
    }

    /// <summary>What one ping actually costs: mean and worst microseconds over every ping this run has sent.</summary>
    /// <remarks>
    /// Held against the sweep's wall time: a mean of a few microseconds with a sweep of hundreds of milliseconds says the sends are
    /// cheap and the SCHEDULING is not, which no amount of making the send faster would fix.
    /// </remarks>
    public (double MeanUs, double WorstUs, long Count) PingCost
    {
        get
        {
            var sends = Volatile.Read(ref _pingSends);
            var total = Volatile.Read(ref _pingTicksTotal);
            var worst = Volatile.Read(ref _pingTicksWorst);
            var perTick = 1_000_000.0 / Stopwatch.Frequency;
            return (sends == 0 ? 0 : total * perTick / sends, worst * perTick, sends);
        }
    }

    /// <summary>Records a ping sweep's duration.</summary>
    /// <param name="ms">How long the sweep took.</param>
    private void NotePingSweep(double ms)
    {
        lock (_reportLock)
        {
            _pingSweepMs = ms;
            _pingSweepWorstMs = Math.Max(_pingSweepWorstMs, ms);
        }
    }

    /// <summary>Records a drive sweep's duration.</summary>
    /// <param name="ms">How long the sweep took.</param>
    private void NoteDriveSweep(double ms)
    {
        lock (_reportLock)
        {
            _driveSweepMs = ms;
            _driveSweepWorstMs = Math.Max(_driveSweepWorstMs, ms);
        }
    }

    /// <summary>Sends one bot's ping, swallowing what a dead socket throws.</summary>
    /// <param name="bot">The bot, which may be null or already gone.</param>
    /// <param name="ct">Cancels the send.</param>
    /// <returns>The send.</returns>
    /// <remarks>
    /// Separate from <see cref="DriveOneAsync"/> and deliberately doing nothing else: this is the pass whose latency decides
    /// whether the fleet stays connected, so nothing that can be deferred belongs in it.
    /// </remarks>
    private async Task PingOneAsync(Bot bot, CancellationToken ct)
    {
        if (bot == null || !bot.Client.IsConnected)
        {
            return;
        }

        try
        {
            // Timed individually, because the sweep's wall time cannot tell a ping that COSTS a long time from a ping that merely
            // WAITED a long time to be scheduled, and those are opposite fixes.
            var at = Stopwatch.GetTimestamp();
            await bot.Client.SendPingAsync(ct).ConfigureAwait(false);
            var took = Stopwatch.GetTimestamp() - at;
            Interlocked.Add(ref _pingTicksTotal, took);
            Interlocked.Increment(ref _pingSends);
            var worst = Volatile.Read(ref _pingTicksWorst);
            while (took > worst && Interlocked.CompareExchange(ref _pingTicksWorst, took, worst) != worst)
            {
                worst = Volatile.Read(ref _pingTicksWorst);
            }
        }
        catch (Exception)
        {
            // One bot's socket going away must not stop the rest of the sweep. The disconnect is counted by its own handler.
        }
    }

    /// <summary>Drives one bot for one tick: the ping, and whatever else this tick asks of it.</summary>
    /// <param name="bot">The bot.</param>
    /// <param name="position">Its one-based position in the fleet, which phases the tour's stagger.</param>
    /// <param name="touring">Whether cameras are touring planets this run.</param>
    /// <param name="tourPeriod">Ticks between a camera's planet switches.</param>
    /// <param name="moveRegions">Whether this tick moves the god cameras' regions.</param>
    /// <param name="wandering">Whether players are walking this run.</param>
    /// <param name="thinking">Whether players are deciding for themselves this run.</param>
    /// <param name="ct">Cancels the sends.</param>
    /// <remarks>
    /// <b>Every exception is contained here rather than at the batch.</b> These run under a <c>WhenAll</c>, which reports only the
    /// first fault and would let one bot's dead socket cancel the rest of its batch — so the per-bot guarantee the old loop made with
    /// a try/catch inside the body has to be made inside the task instead.
    /// </remarks>
    private async Task DriveOneAsync(Bot bot, int position, bool touring, long tourPeriod, bool moveRegions, bool wandering, bool thinking,
        CancellationToken ct)
    {
        // Null-checked as well as connection-checked, because a shrink lowers the count before it empties the slots: the driver can
        // read a count that still includes a bot the reconciler is in the middle of retiring. Lowering the count first is the right
        // order — it makes the bot unreachable before its socket closes — and this is the other half of that bargain.
        if (bot == null || !bot.Client.IsConnected)
        {
            return;
        }

        try
        {
            // The ping is NOT here any more: it goes out in its own pass ahead of this one, because queueing it behind the
            // movement of every earlier batch is what pushed sessions past the server's silence window. See PingLoopAsync.
            //
            // Staggered by bot: every camera switching on one tick would be N whole-planet RESETs in one server tick.
            if (touring && (Ticks + position) % tourPeriod == 0)
            {
                await bot.SendNextPlanetAsync(_options, ct).ConfigureAwait(false);
            }

            if (moveRegions)
            {
                await bot.SendRegionAsync(Ticks, _options, ct).ConfigureAwait(false);
            }

            if (wandering)
            {
                await bot.WanderAsync(Ticks, _options, ct).ConfigureAwait(false);
            }

            if (thinking)
            {
                await bot.ThinkAsync(Ticks, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The batch's own check returns from the loop; one cancelled send must not fault its neighbours' WhenAll.
        }
        catch (ObjectDisposedException)
        {
            // <b>A lifecycle race, not a catalog mismatch, and it must not be filed as one.</b> ObjectDisposedException derives from
            // InvalidOperationException, so the arm below took it and the panel declared "PROTOCOL FAULTS … the figures below are NOT
            // a clean measurement" over a bot that was retired mid-tick: the driver reads the count and then the slot, so it can hold
            // a reference to a session the reconciler has just disposed. That is the window the count-first ordering accepts, and the
            // send failing is the intended outcome. Counted as a disconnect by its own handler, not as our send bug.
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

        /// <summary>The wanderer's state: its heading in radians, when it next turns, and the stream it draws turns from.</summary>
        private float _heading;
        private long _nextTurnTick;
        private uint _wander;

        public long ToursAsked;
        public long RealmSwitches;
        public long IntentsSent;

        /// <summary>Shuttle boardings this bot has asked for, and hunts it has begun: what the brain actually did.</summary>
        public long BoardingsAsked;

        public long HuntsStarted;

        /// <summary>
        /// Missions (lair-objectives.md): takes sent, missions its journal listed for the first time, completions it stated, cancels sent.
        /// </summary>
        public long MissionTakesSent;

        public long MissionsTaken;
        public long MissionsCompleted;
        public long MissionsCancelled;

        /// <summary>The journal as last read: what the brain is told about the mission it holds.</summary>
        private volatile MissionView _missions = MissionView.None;

        // Read like a browser's: GET /swg/missions with the bot's own token whenever the body's owner field `journal` moves.
        private static readonly HttpClient JournalHttp = new() { Timeout = TimeSpan.FromSeconds(10) };
        private readonly Uri _missionsUri;
        private readonly string _token;
        private ArchetypePlan _journalFor;
        private int _journalOrdinal = -1;
        private double _journalAsked = double.NaN;
        private long _journalRetryAt;
        private int _journalBusy;
        private bool _journalPrimed;
        private readonly bool _takesMissions;
        private long _stanceAskedAt = long.MinValue / 2;
        private readonly HashSet<string> _missionsSeen = [];
        private readonly HashSet<string> _completedSeen = [];

        /// <summary>Tips sent, and tips due with nobody named within reach.</summary>
        public long TipsSent;

        public long TipsNobodyNear;

        /// <summary>The tip period in ticks, 0 for none; the tick the next one is due; and the bot's own stream for its jitter and amounts.</summary>
        private readonly long _tipEveryTicks;

        private readonly long _ticksPerSecond;
        private long _nextTipTick;
        private uint _tipRandom;

        /// <summary>The most creatures this bot has ever had in view at once, so an empty sky can be told from a blind scan.</summary>
        public int CreaturesSeenMax;

        /// <summary>How many shuttleports this brain has learned: 2 is the fewest that lets it fly anywhere.</summary>
        /// <returns>The count.</returns>
        public int PortsKnown() => _brain?.Facilities.Count ?? 0;

        /// <summary>What this bot's brain did with the city: swoop asks, doors crossed, terminals used, walks searched.</summary>
        public (long Mounts, long Doors, long Used, long Paths) Errands() =>
            _brain == null ? default : (_brain.MountsAsked, _brain.DoorsCrossed, _brain.FacilitiesUsed, _brain.PathsPlanned);

        /// <summary>This bot's own decision loop and the eye that feeds it; null unless the run is thinking.</summary>
        private readonly BotBrain _brain;

        private readonly Perception _eyes;

        /// <summary>The one bot whose decisions are printed, from <c>SWG_BOT_TRACE</c> (an index, or <c>all</c>); -1 for none.</summary>
        private static readonly int TraceIndex = int.TryParse(Environment.GetEnvironmentVariable("SWG_BOT_TRACE"), out var i) ? i : -1;

        private static readonly bool TraceAll = Environment.GetEnvironmentVariable("SWG_BOT_TRACE") == "all";
        public readonly NewsCounter News = new();

        public Bot(int index, BotSwarmOptions options, BotAssets assets = null)
        {
            // Deterministic from the index, so a run is reproducible and two bots are never in the same place. The golden ratio keeps successive angles from
            // clustering, which a plain index × constant does not.
            var golden = 2.399963229728653;
            var radius = 400 + (index % 16 * 220);
            _centreX = Math.Cos(index * golden) * radius;
            _centreY = Math.Sin(index * golden) * radius;
            _phase = index * golden;

            // The wanderer's own stream, seeded from the index so a run reproduces and no two bots walk in step. Offset by the index so
            // the first turn is staggered too: every avatar turning on one tick would be N commands in a single server tick, which is
            // the mistake the planet tour already staggers to avoid.
            _wander = (uint)((index * 2654435761u) | 1u);
            if (options.Brain)
            {
                // Its own stream, from its own index, for the reason the wanderer's is: a run has to reproduce, and a thousand bots
                // sharing one stream would decide in lockstep. A different multiplier from the wanderer's so a bot's course and its
                // choices are not the same sequence read twice.
                _brain = new BotBrain(BotMap.Cities, (uint)((index * 2246822519u) | 1u), options.TickHz, assets);
                _eyes = new Perception();
                _ticksPerSecond = Math.Max(1, options.TickHz);
                _tipEveryTicks = (long)Math.Round(options.TipSeconds * _ticksPerSecond);
                _tipRandom = (uint)((index * 3266489917u) | 1u);
                _nextTipTick = NextTipDelay();
                _token = options.TokenFor?.Invoke(index);
                _missionsUri = BotProvisioner.AuthBaseFrom(options.Endpoint) is { } server ? new Uri(server, "swg/missions") : null;

                // A bot that cannot read its journal never learns it holds one: it would take a mission at every visit and never go and do it.
                _takesMissions = options.Missions && _missionsUri != null && !string.IsNullOrEmpty(_token);
            }
            _heading = (float)((index * golden) % (Math.PI * 2));

            // <b>Zero, so the FIRST heading is sent on the first tick this bot is driven.</b> This was `index % dwellTicks`, which
            // staggered the first intent as well as the turns — and the stagger is only needed for the turns. With a long dwell that
            // delayed the first step by the whole stagger: at an 8 000-second dwell, bot 500 would not have walked for two minutes,
            // and a fleet set up that way looks exactly like a fleet whose brain does not work. The turns are staggered where they
            // are decided, by jittering each dwell.
            _nextTurnTick = 0;

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

        /// <summary>Walks: renews the current heading, and turns onto a new one when this bot's dwell has run out.</summary>
        /// <param name="tick">The driver's tick.</param>
        /// <param name="options">The swarm's configuration.</param>
        /// <param name="ct">Cancels the send.</param>
        /// <remarks>
        /// <para>
        /// <b>Re-sent every tick, and that is a requirement rather than laziness.</b> The server turns a heading plus a speed class into
        /// a destination <c>max(speed, 1)</c> metres ahead — about one second of travel — so an intent that stops being renewed is simply
        /// reached, and the avatar halts where it got to. The client SDK's own rule is the same: send on change, plus a periodic refresh.
        /// <c>MoveDir</c> is coalesced <c>LatestPerSession</c> and rate-limited at 30/s with a burst of 60, so at the driver's 4 Hz this
        /// is an eighth of the allowance.
        /// </para>
        /// <para>
        /// The turn is a bounded deflection rather than a fresh angle, which is what "do not change direction too often" means
        /// geometrically: a new random heading every dwell is a random walk that stays near where it started, while a limited turn
        /// produces a path that actually goes somewhere and crosses cells — the thing worth loading the spatial index with.
        /// </para>
        /// </remarks>
        public async Task WanderAsync(long tick, BotSwarmOptions options, CancellationToken ct)
        {
            if (Client.Plan?.CommandByName(MoveDirCommand) == null)
            {
                return;
            }

            if (tick >= _nextTurnTick)
            {
                // xorshift32, because this is a direction for a bot and not a source of entropy: it is deterministic from the seed,
                // needs no allocation and no lock, and every bot owns its own.
                _wander ^= _wander << 13;
                _wander ^= _wander >> 17;
                _wander ^= _wander << 5;

                // Up to ±60°, so a turn is a change of course and not a reversal.
                var deflect = ((_wander & 0xFFFFu) / 65536f * 2f - 1f) * (MathF.PI / 3f);
                _heading = MathF.IEEERemainder(_heading + deflect, MathF.PI * 2);

                // The dwell itself varies by up to half, so the fleet does not fall into lockstep after the first turn.
                var dwell = options.WanderSeconds * Math.Max(1, options.TickHz);
                var jitter = ((_wander >> 16) / 65536.0 * 0.5) + 0.75;
                _nextTurnTick = tick + Math.Max(1, (long)Math.Round(dwell * jitter));
            }

            // The field names as the server declares them (TatooineReplication.cs: Field(m => m.Heading, Codec.F16, "heading"), and so
            // on). RecordValues.For throws on a name the catalog does not carry, which is how --god-tour spent its whole life sending
            // nothing with "Realm" for "realm" — so these are copied from the declaration rather than from memory.
            var values = new RecordValues
            {
                ["heading"] = FieldValue.Of((double)_heading),
                ["speedClass"] = FieldValue.Of((double)options.WanderSpeedClass),

                // 0: the intent changed on the tick boundary as far as this bot is concerned. A14b's phase exists to place a human's
                // keypress inside a tick, and a timer has nothing finer to report.
                ["changePhase"] = FieldValue.Of(0.0),

                // ×1 at full throttle: a bot moves at what the server grants, as a person who never pressed Shift+2..4 does.
                ["boost"] = FieldValue.Of(1.0),
                ["throttle"] = FieldValue.Of(0.0),
            };

            await Client.SendCommandAsync(MoveDirCommand, values, ct).ConfigureAwait(false);
            IntentsSent++;
        }

        /// <summary>Decides from what this bot was sent, and sends what it decided.</summary>
        /// <param name="tick">The driver's tick, which the timed goals count in.</param>
        /// <param name="ct">Cancels the send.</param>
        /// <remarks>
        /// <b>Perception, decision, send — and only the last of the three touches the wire.</b> The decision is a pure function of a
        /// <c>Perceived</c>, so it is tested without a server; this method is the plumbing that cannot be.
        /// <para>
        /// Every intent below is a command a browser has. That is the property this phase exists to protect: the server has no branch
        /// on bot-ness, so there is nothing for it to tell apart.
        /// </para>
        /// </remarks>
        public async Task ThinkAsync(long tick, CancellationToken ct)
        {
            if (_brain == null || Client.Plan == null)
            {
                return;
            }

            var world = _eyes.Read(Client.Store, _brain.Facilities);
            if (world.NearestCreature != 0)
            {
                CreaturesSeenMax = Math.Max(CreaturesSeenMax, 1);
            }

            // The mission held, as the journal last said, and its lair as the bot sees it once near (lair-objectives.md B-2, B-3).
            PollJournal(tick);
            var missions = _missions;
            world = world with
            {
                MissionsHeld = missions.Held,
                HasMissionLair = missions.LairKnown,
                MissionLairX = missions.LairX,
                MissionLairZ = missions.LairZ,
            };
            if (missions.LairKnown && world.HasBody && Routing.Distance(world.X, world.Z, missions.LairX, missions.LairZ) <= BotBrain.NoticeM * 2)
            {
                var lair = _eyes.LairAt(Client.Store, missions.LairX, missions.LairZ, MissionLairMatchM, out var guards);
                world = world with { MissionLair = lair, MissionLairGuards = guards };
            }

            var before = _brain.Goal;
            var intent = _brain.Decide(in world, tick);
            if (TraceAll || Index == TraceIndex)
            {
                // A brain is read from what it did, tick by tick: one bot's decisions, to stdout, when SWG_BOT_TRACE names its index.
                Console.WriteLine(
                    $"  trace {Index,3} {tick,6} realm {world.Realm} at ({world.X:F1}, {world.Z:F1}) mounted {(world.Mounted ? 1 : 0)} "
                    + $"{_brain.Goal,-10} {intent.Kind,-6} h {intent.Heading:F2} c {intent.SpeedClass} to ({intent.X:F1}, {intent.Z:F1}) on {intent.On} "
                    + _brain.LegState);
                if (tick % 200 == 1 && world.Structures != null)
                {
                    // What it stands among, now and then: a body that never moves is read off what is around it.
                    var around = new List<SeenStructure>();
                    world.Structures.CopyTo(around);
                    foreach (var s in around)
                    {
                        if (Routing.Distance(world.X, world.Z, s.X, s.Z) < 25d)
                        {
                            Console.WriteLine($"  trace {Index,3} {tick,6}   near kind {s.Kind} asset {s.Asset} yaw {s.Yaw} at ({s.X:F1}, {s.Z:F1})");
                        }
                    }
                }
            }

            // Counted on the TRANSITION into hunting, not on each SetTarget. A hunt re-names its target every tick it
            // is in strike range, so counting sends made a dozen bots look like five hundred hunts — and this counter
            // exists because an earlier version's silence was invisible, so a counter that overstates is worse than
            // none at all.
            if (_brain.Goal == Goal.Hunting && before != Goal.Hunting)
            {
                HuntsStarted++;
            }

            // A target is fought only in a stance, and a swoop, a door, a flight or a clone puts it away — the ride out to a mission's lair did, every time,
            // and the bot then named its target for minutes without a shot fired (2026-10-26). So the stance is read, not remembered: drawn whenever the bot
            // is about to attack without it. A server without the command is one where a target alone is a fight.
            // Not while still mounted — the swoop puts it away again — and once per two seconds, the time its replication takes to come back.
            if (intent.Kind == IntentKind.Attack && world.Stance != (int)PistolStance && !world.Mounted && tick - _stanceAskedAt >= 8
                && Client.Plan.CommandByName(SetStanceCommand) != null)
            {
                _stanceAskedAt = tick;
                await Client.SendCommandAsync(SetStanceCommand, new RecordValues { ["stance"] = FieldValue.Of(PistolStance) }, ct)
                    .ConfigureAwait(false);
                IntentsSent++;
            }

            switch (intent.Kind)
            {
                case IntentKind.Move when Client.Plan.CommandByName(MoveDirCommand) != null:
                    await Client.SendCommandAsync(
                        MoveDirCommand,
                        new RecordValues
                        {
                            ["heading"] = FieldValue.Of((double)intent.Heading),
                            ["speedClass"] = FieldValue.Of((double)intent.SpeedClass),
                            ["changePhase"] = FieldValue.Of(0.0),
                            ["boost"] = FieldValue.Of(1.0),
                            ["throttle"] = FieldValue.Of(0.0),
                        },
                        ct).ConfigureAwait(false);
                    IntentsSent++;
                    break;

                case IntentKind.Attack when Client.Plan.CommandByName(SetTargetCommand) != null:
                    await Client.SendCommandAsync(
                        SetTargetCommand,
                        new RecordValues { ["netId"] = FieldValue.Of((double)intent.NetId) },
                        ct).ConfigureAwait(false);
                    IntentsSent++;
                    break;

                case IntentKind.MoveTo when Client.Plan.CommandByName(MoveToCommand) != null:
                    await Client.SendCommandAsync(
                        MoveToCommand,
                        new RecordValues { ["x"] = FieldValue.Of((double)intent.X), ["z"] = FieldValue.Of((double)intent.Z) },
                        ct).ConfigureAwait(false);
                    IntentsSent++;
                    break;

                case IntentKind.Mount when Client.Plan.CommandByName(SetMountCommand) != null:
                    await Client.SendCommandAsync(
                        SetMountCommand,
                        new RecordValues { ["on"] = FieldValue.Of(intent.On ? 1.0 : 0.0) },
                        ct).ConfigureAwait(false);
                    IntentsSent++;
                    break;

                case IntentKind.Board when Client.Plan.CommandByName(BoardShuttleCommand) != null:
                    // The one place the brain's regions meet the wire's city index, and the only -1 in the bot: the
                    // brain carries a city plus one so that absence is 0, and `BoardShuttle` names a bare city.
                    await Client.SendCommandAsync(
                        BoardShuttleCommand,
                        new RecordValues { ["destCity"] = FieldValue.Of((double)(intent.Region - 1)) },
                        ct).ConfigureAwait(false);
                    IntentsSent++;
                    BoardingsAsked++;
                    break;

                case IntentKind.TakeMission when _takesMissions && Client.Plan.CommandByName(TakeMissionCommand) != null:
                    // The offer whose lair is nearest, of those listed at the terminal it stands at: what a person picks from the sorted list.
                    var offer = _eyes.BestOffer(Client.Store, world.X, world.Z, OfferReachM, out var serial);
                    if (offer != 0)
                    {
                        await Client.SendCommandAsync(
                            TakeMissionCommand,
                            new RecordValues { ["offer"] = FieldValue.Of((double)offer), ["serial"] = FieldValue.Of((double)serial) },
                            ct).ConfigureAwait(false);
                        IntentsSent++;
                        MissionTakesSent++;
                    }

                    break;

                case IntentKind.AbandonMission when Client.Plan.CommandByName(AbandonMissionCommand) != null:
                    if (missions.FirstId != 0)
                    {
                        await Client.SendCommandAsync(
                            AbandonMissionCommand,
                            new RecordValues { ["mission"] = FieldValue.OfUInt64(missions.FirstId) },
                            ct).ConfigureAwait(false);
                        IntentsSent++;
                        MissionsCancelled++;
                    }

                    break;

                default:
                    break;   // nothing to say this tick, which is what idling and waiting look like on the wire
            }

            await TipAsync(tick, world, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Reads the journal when the body's <c>journal</c> owner field moved since the last read — the browser's pattern (missions.md § 4) — or a failed
        /// read is due again. One read at a time; its answer lands in <see cref="_missions"/>.
        /// </summary>
        private void PollJournal(long tick)
        {
            var self = Client.Store?.Self;
            if (_missionsUri == null || string.IsNullOrEmpty(_token) || self?.Archetype == null)
            {
                return;
            }

            if (!ReferenceEquals(self.Archetype, _journalFor))
            {
                _journalFor = self.Archetype;
                _journalOrdinal = -1;
                foreach (var field in self.Archetype.OwnerFields)
                {
                    if (field.Name == "journal")
                    {
                        _journalOrdinal = field.Ordinal;
                    }
                }
            }

            if (_journalOrdinal < 0 || _journalOrdinal >= self.Numbers.Length || self.Numbers[_journalOrdinal] is not { Length: > 0 } values)
            {
                return;
            }

            var rev = values[0];
            if ((rev == _journalAsked && tick < _journalRetryAt) || Interlocked.CompareExchange(ref _journalBusy, 1, 0) != 0)
            {
                return;
            }

            _journalAsked = rev;
            _journalRetryAt = long.MaxValue;
            _ = ReadJournalAsync(tick);
        }

        private async Task ReadJournalAsync(long tick)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, _missionsUri);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
                using var response = await JournalHttp.SendAsync(request).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _journalRetryAt = tick + 12;
                    return;
                }

                // The first read only learns what was already there — a mission held from an earlier run, completions the server still remembers — so
                // this run's counts are this run's (review, 2026-10-26).
                var view = MissionView.Parse(
                    await response.Content.ReadAsStringAsync().ConfigureAwait(false),
                    _missionsSeen,
                    _completedSeen,
                    count: _journalPrimed,
                    out var taken,
                    out var completed);
                _journalPrimed = true;
                Interlocked.Add(ref MissionsTaken, taken);
                Interlocked.Add(ref MissionsCompleted, completed);
                _missions = view;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException
                                          or FormatException)
            {
                _journalRetryAt = tick + 12;   // three seconds at the usual 4 Hz; the next revision asks anyway
            }
            finally
            {
                Volatile.Write(ref _journalBusy, 0);
            }
        }

        /// <summary>
        /// A tip when one is due (E0-11): a few credits to the nearest named player within <see cref="TipReachM"/>, through the command a browser's
        /// <c>/tip</c> sends. Nobody near puts it off a second rather than a whole period, so a bot walking into a crowd tips soon after.
        /// </summary>
        private async Task TipAsync(long tick, Perceived world, CancellationToken ct)
        {
            if (_tipEveryTicks == 0 || tick < _nextTipTick || !world.HasBody || Client.Plan.CommandByName(GiveCreditsCommand) == null)
            {
                return;
            }

            var target = _eyes.NearestNamedPlayer(Client.Store, world.X, world.Z, TipReachM);
            if (target == 0)
            {
                TipsNobodyNear++;
                _nextTipTick = tick + _ticksPerSecond;
                return;
            }

            var amount = 1 + (int)(NextTipRandom() % MaxBotTip);
            await Client.SendCommandAsync(
                GiveCreditsCommand,
                new RecordValues { ["target"] = FieldValue.Of((double)target), ["amount"] = FieldValue.Of((double)amount) },
                ct).ConfigureAwait(false);
            TipsSent++;
            _nextTipTick = tick + NextTipDelay();
        }

        /// <summary>Half to one and a half periods, from the bot's own stream: a fleet started together does not tip in step.</summary>
        private long NextTipDelay() => _tipEveryTicks == 0 ? 0 : (_tipEveryTicks / 2) + (long)(NextTipRandom() % (ulong)Math.Max(1, _tipEveryTicks));

        private ulong NextTipRandom()
        {
            // xorshift32, the wanderer's generator.
            _tipRandom ^= _tipRandom << 13;
            _tipRandom ^= _tipRandom >> 17;
            _tipRandom ^= _tipRandom << 5;
            return _tipRandom;
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

/// <summary>What a bot's journal last said about the mission it holds (lair-objectives.md B-2).</summary>
/// <param name="Held">How many it holds.</param>
/// <param name="LairKnown">Whether the first one's lair has a position: it does not while being drawn or moved.</param>
/// <param name="LairX">Its lair, east.</param>
/// <param name="LairZ">Its lair, north.</param>
/// <param name="FirstId">Its id, for a cancel; 0 for none.</param>
public sealed record MissionView(int Held, bool LairKnown, float LairX, float LairZ, ulong FirstId)
{
    public static MissionView None { get; } = new(0, false, 0f, 0f, 0);

    /// <summary>
    /// Reads a <c>GET /swg/missions</c> answer: the first mission held and its lair, and — added to <paramref name="seen"/> and <paramref name="done"/> —
    /// the missions and completions not met before.
    /// </summary>
    /// <param name="json">The answer.</param>
    /// <param name="seen">Mission ids met so far.</param>
    /// <param name="done">Completion ids met so far.</param>
    /// <param name="count">Whether new ids count: false on the first read, which only learns what was already there.</param>
    /// <param name="taken">Missions met for the first time, when counted.</param>
    /// <param name="completed">Completions met for the first time, when counted.</param>
    /// <returns>What the brain is told.</returns>
    public static MissionView Parse(string json, HashSet<string> seen, HashSet<string> done, bool count, out int taken, out int completed)
    {
        taken = 0;
        completed = 0;
        using var doc = JsonDocument.Parse(json);
        var held = 0;
        var view = None;
        foreach (var m in doc.RootElement.GetProperty("missions").EnumerateArray())
        {
            var id = m.GetProperty("id").GetString() ?? "0";
            taken += seen.Add(id) && count ? 1 : 0;
            if (held++ == 0)
            {
                var lair = m.GetProperty("lair");
                var raw = ulong.Parse(id, CultureInfo.InvariantCulture);
                view = lair.ValueKind == JsonValueKind.Object
                    ? new MissionView(1, true, lair.GetProperty("x").GetSingle(), lair.GetProperty("z").GetSingle(), raw)
                    : new MissionView(1, false, 0f, 0f, raw);
            }
        }

        foreach (var c in doc.RootElement.GetProperty("completed").EnumerateArray())
        {
            if (c.GetProperty("id").GetString() is { } id)
            {
                completed += done.Add(id) && count ? 1 : 0;
            }
        }

        return view with { Held = held };
    }
}

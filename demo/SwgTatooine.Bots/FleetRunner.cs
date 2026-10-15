using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace SwgTatooine.Bots;

/// <summary>Everything a fleet run needs, in the shape a form can edit.</summary>
/// <remarks>
/// <b>A mutable class and not the <c>init</c>-only <see cref="BotSwarmOptions"/>, because this one is bound to a UI.</b> The swarm's
/// options are immutable on purpose — a run's configuration must not change under it — so this is the editable thing that produces
/// one, and the conversion happens once at <see cref="FleetRunner.StartAsync"/>. Keeping them separate is what stops a control
/// panel from mutating a run in flight and reporting numbers against a configuration that no longer describes them.
/// </remarks>
public sealed class FleetSettings
{
    /// <summary>Where the server is.</summary>
    public string Endpoint { get; set; } = "ws://127.0.0.1:9100/ws";

    /// <summary>How many sessions to open.</summary>
    public int Count { get; set; } = 100;

    /// <summary><c>god</c> for an anonymous camera, <c>player</c> for a real account.</summary>
    public string Kind { get; set; } = "god";

    /// <summary>How often the shared driver fires, in hertz.</summary>
    public int TickHz { get; set; } = 4;

    /// <summary>Sessions opened per second during the ramp; 0 leaves the batch in charge.</summary>
    public int ConnectRatePerSecond { get; set; } = 200;

    /// <summary>A camera's region edge, in metres.</summary>
    public double RegionRadiusM { get; set; } = 256;

    /// <summary>The accounts file, required for <c>player</c>.</summary>
    public string AccountsFile { get; set; } = "bots.jsonl";

    /// <summary>How many signups and logins run at once.</summary>
    public int SignupConcurrency { get; set; } = BotProvisioner.DefaultConcurrency;

    /// <summary>One-way delay to shape onto the box's interface, in milliseconds; 0 shapes nothing.</summary>
    public int LatencyMs { get; set; }

    /// <summary>Jitter around that delay, in milliseconds.</summary>
    public int JitterMs { get; set; }

    /// <summary>The interface to shape. Empty lets the shaper pick the default route's.</summary>
    public string NetworkInterface { get; set; } = string.Empty;

    /// <summary>Whether this configuration wants real accounts.</summary>
    public bool IsPlayer => string.Equals(Kind, "player", StringComparison.Ordinal);

    /// <summary>
    /// A copy, so a run is not reading a form somebody is still typing into.
    /// </summary>
    /// <returns>An independent copy.</returns>
    /// <remarks>
    /// <b>This is not defensive style, it is a defect that was found.</b> The control panel registers one of these as a DI
    /// singleton and binds it straight to its inputs, and <c>FleetRunner</c> stored the REFERENCE while documenting that it had
    /// copied — so a second browser tab, or the same operator editing while a run was up, mutated the configuration of a run in
    /// flight. The numbers were then being reported against a configuration that no longer described them, which for a load
    /// generator is the whole product spoiled.
    /// </remarks>
    public FleetSettings Clone() => (FleetSettings)MemberwiseClone();
}

/// <summary>Where a run is in its life.</summary>
public enum FleetPhase
{
    /// <summary>Nothing running.</summary>
    Idle = 0,

    /// <summary>Signing up or logging in the population.</summary>
    Provisioning,

    /// <summary>Opening sessions.</summary>
    Connecting,

    /// <summary>Open and being driven.</summary>
    Running,

    /// <summary>Closing down.</summary>
    Stopping,

    /// <summary>Stopped because something went wrong. <see cref="FleetSnapshot.Fault"/> says what.</summary>
    Faulted,
}

/// <summary>What a panel renders: one consistent read of a run.</summary>
/// <param name="Phase">Where the run is.</param>
/// <param name="Requested">Sessions asked for.</param>
/// <param name="Opened">Sessions the ramp opened.</param>
/// <param name="Connected">Sessions open right now.</param>
/// <param name="Provisioned">Accounts ready, during and after provisioning.</param>
/// <param name="Messages">Messages received across the fleet.</param>
/// <param name="Bytes">Bytes received across the fleet.</param>
/// <param name="BytesPerSessionPerSecond">The capacity number: wire cost per session.</param>
/// <param name="Faults">Receive-loop faults.</param>
/// <param name="ProtocolFaults">Faults that are this program's own bug, not the network's.</param>
/// <param name="ServerTickP50">The server's own tick p50, in ms, as it reports it over STATS.</param>
/// <param name="ServerTickP99">The server's own tick p99, in ms.</param>
/// <param name="Disconnects">Close codes seen, and how many of each.</param>
/// <param name="Elapsed">How long the run has been up.</param>
/// <param name="Shaping">The delay actually shaped onto the interface, or <see langword="null"/>.</param>
/// <param name="Fault">Why the run faulted, or <see langword="null"/>.</param>
public readonly record struct FleetSnapshot(
    FleetPhase Phase,
    int Requested,
    int Opened,
    int Connected,
    int Provisioned,
    long Messages,
    long Bytes,
    double BytesPerSessionPerSecond,
    int Faults,
    int ProtocolFaults,
    double ServerTickP50,
    double ServerTickP99,
    IReadOnlyDictionary<ushort, int> Disconnects,
    TimeSpan Elapsed,
    string Shaping,
    string Fault);

/// <summary>
/// One fleet run, start to stop, as an object a console or a control panel can both drive.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because the run sequence was trapped in top-level statements.</b> Provision, then ramp, then drive, then report
/// is the same sequence whether a shell asked for it or a web form did, and the alternative to this class was the control panel
/// growing its own copy — two provisioning paths, one of which would be the one nobody runs in CI.
/// </para>
/// <para>
/// <b>It owns the swarm's lifetime and refuses to run twice.</b> A panel has a button, and a button gets pressed twice: a second
/// run against a live swarm would open a second population on top of the first and report the sum as one measurement.
/// </para>
/// </remarks>
public sealed class FleetRunner : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly List<string> _log = [];

    /// <summary>
    /// When the run started, as a <see cref="Stopwatch"/> timestamp; <c>0</c> when none has.
    /// </summary>
    /// <remarks>
    /// <b>A timestamp and not a <see cref="Stopwatch"/>, because the panel reads it from another thread.</b> <c>Stopwatch</c> is
    /// documented as not thread-safe and <c>Elapsed</c> reads three fields non-atomically, so a UI thread sampling it while the run
    /// thread calls <c>Restart</c> can get a duration from neither run — and that duration is the DIVISOR of the headline
    /// bytes-per-session figure, so a torn read does not look like a glitch, it looks like a measurement.
    /// </remarks>
    private long _startedAt;

    private long _stoppedAt;

    /// <summary>
    /// The interface <see cref="NetemShaper"/> actually shaped, so teardown unshapes that one.
    /// </summary>
    /// <remarks>
    /// Not re-derived at teardown: passing an empty device re-resolved the route, and a route that had changed in between left
    /// the shaped interface shaped — so the next run measured a delay nobody had asked for and nothing reported it.
    /// </remarks>
    private string _shapedDevice;

    private BotSwarm _swarm;
    private BotAccountStore _accounts;
    private CancellationTokenSource _stopping;
    private Task _run;
    private FleetPhase _phase;
    private FleetSettings _settings;
    private int _opened;
    private int _provisioned;
    private long _bytesAtStart;
    private string _shaping;
    private string _fault;

    /// <summary>How many log lines are kept. Bounded, because a panel left open for a day must not grow without end.</summary>
    public const int LogCapacity = 500;

    /// <summary>The floor between two unforced change notifications, in milliseconds.</summary>
    private const int NotifyIntervalMs = 250;

    private long _lastNotify;

    /// <summary>Raised whenever the snapshot would read differently, so a panel can re-render without polling hard.</summary>
    public event Action Changed;

    /// <summary>Whether a run is up.</summary>
    public bool Busy
    {
        get
        {
            lock (_gate)
            {
                return _phase is FleetPhase.Provisioning or FleetPhase.Connecting or FleetPhase.Running or FleetPhase.Stopping;
            }
        }
    }

    /// <summary>The run log, newest last.</summary>
    /// <returns>A copy, so a renderer cannot be walking it while the run appends.</returns>
    public string[] Log()
    {
        lock (_gate)
        {
            return [.. _log];
        }
    }

    /// <summary>One consistent read of the run.</summary>
    /// <returns>The snapshot.</returns>
    public FleetSnapshot Snapshot()
    {
        // <b>Every field this snapshot claims to present consistently is read in ONE critical section.</b> Before, only the phase
        // and the population were, and the rest were plain reads racing the run thread's plain writes -- which on arm64, which is
        // what the bot box runs, can genuinely show `Running` with nothing opened. The swarm's own counters are read outside,
        // because they are the swarm's business and it publishes them safely itself.
        BotSwarm swarm;
        FleetPhase phase;
        int requested;
        int opened;
        int provisioned;
        long bytesAtStart;
        string shaping;
        string fault;
        TimeSpan elapsed;
        lock (_gate)
        {
            swarm = _swarm;
            phase = _phase;
            requested = _settings?.Count ?? 0;
            opened = _opened;
            provisioned = _provisioned;
            bytesAtStart = _bytesAtStart;
            shaping = _shaping;
            fault = _fault;
            elapsed = ElapsedLocked();
        }

        if (swarm == null)
        {
            return new FleetSnapshot(phase, requested, 0, 0, provisioned, 0, 0, 0, 0, 0, 0, 0, new Dictionary<ushort, int>(), elapsed, shaping, fault);
        }

        var seconds = Math.Max(1.0, elapsed.TotalSeconds);
        var bytes = swarm.BytesReceived;
        var perSession = requested > 0 ? (bytes - bytesAtStart) / seconds / requested : 0;
        var (protocolKinds, _) = swarm.ProtocolFaults();

        return new FleetSnapshot(
            phase,
            requested,
            opened,
            swarm.Connected,
            provisioned,
            swarm.MessagesReceived,
            bytes,
            perSession,
            swarm.Faults,
            protocolKinds,
            swarm.ServerMetric("typhon.tick.duration.p50"),
            swarm.ServerMetric("typhon.tick.duration.p99"),
            swarm.Disconnects,
            elapsed,
            shaping,
            fault);
    }

    /// <summary>How long the run has been up, or how long it ran. The caller holds <see cref="_gate"/>.</summary>
    /// <returns>The duration.</returns>
    private TimeSpan ElapsedLocked()
    {
        if (_startedAt == 0)
        {
            return TimeSpan.Zero;
        }

        var until = _stoppedAt == 0 ? Stopwatch.GetTimestamp() : _stoppedAt;
        return Stopwatch.GetElapsedTime(_startedAt, until);
    }

    /// <summary>
    /// Provisions, shapes, ramps and starts driving. Returns once the population is open.
    /// </summary>
    /// <param name="settings">The run's configuration, copied at this point and not read again.</param>
    /// <param name="ct">Cancels the start.</param>
    /// <returns>How many sessions opened.</returns>
    /// <exception cref="InvalidOperationException">A run is already up.</exception>
    public async Task<int> StartAsync(FleetSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        CancellationTokenSource stopping;
        lock (_gate)
        {
            // Busy reads _phase under this same lock, and `lock` is re-entrant in C#, so calling it here is correct -- but it is
            // read directly to keep that from being something a reader has to know.
            if (_phase is FleetPhase.Provisioning or FleetPhase.Connecting or FleetPhase.Running or FleetPhase.Stopping)
            {
                throw new InvalidOperationException("a fleet is already running; stop it before starting another");
            }

            // COPIED, not aliased: see FleetSettings.Clone. Everything below reads this copy, so the form can be edited while a
            // run is up without the run's own configuration moving under it.
            settings = settings.Clone();
            _settings = settings;
            _phase = FleetPhase.Provisioning;
            _fault = null;
            _opened = 0;
            _provisioned = 0;

            // Reset HERE, with the rest. Left over from the previous run it made the second run's wire figure the difference
            // between an empty swarm and the old one's total -- a large NEGATIVE bytes-per-second on the panel.
            _bytesAtStart = 0;
            _startedAt = 0;
            _stoppedAt = 0;
            _log.Clear();

            // <b>Created BEFORE provisioning, which is the whole point.</b> It used to be created after the ramp, so Stop during
            // provisioning or connecting cancelled nothing: the phase went to Stopping, a null swarm was torn down, the phase went
            // to Idle -- and StartAsync carried on opening sessions and then announced Running. The button was enabled and did
            // nothing, against a fleet that was still growing.
            _stopping?.Dispose();
            _stopping = new CancellationTokenSource();
            stopping = _stopping;
        }

        Notify();
        try
        {
            // Linked, so either the caller's token or the Stop button ends the run.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stopping.Token);
            var runCt = linked.Token;
            var endpoint = new Uri(settings.Endpoint);
            if (settings.LatencyMs > 0)
            {
                // The endpoint's host as the route probe: on the placement 07-bots § 5 prescribes the bot box reaches the server
                // by private IP in the same subnet and may have no route off the VPC at all, so asking about a public address
                // was asking the wrong question.
                var shaped = await NetemShaper
                    .ApplyAsync(settings.NetworkInterface, settings.LatencyMs, settings.JitterMs, endpoint.Host, runCt)
                    .ConfigureAwait(false);
                lock (_gate)
                {
                    _shaping = shaped.Description;
                    _shapedDevice = shaped.Device;
                }

                Say("shaping: " + shaped.Description);
            }

            await ProvisionAsync(settings, endpoint, runCt).ConfigureAwait(false);

            Phase(FleetPhase.Connecting);
            var options = BuildOptions(settings, endpoint);
            var swarm = new BotSwarm(options);
            lock (_gate)
            {
                _swarm = swarm;
                _startedAt = Stopwatch.GetTimestamp();
            }

            var opened = await swarm.StartAsync(runCt).ConfigureAwait(false);
            lock (_gate)
            {
                _opened = opened;
                _bytesAtStart = swarm.BytesReceived;
            }

            Say($"opened {opened} of {settings.Count} sessions");

            if (opened == 0)
            {
                // A fleet that opened nothing has measured nothing, and every number a panel then shows is a well-formatted zero.
                throw new InvalidOperationException($"no session opened against {settings.Endpoint}; check the scheme and the port");
            }

            _run = HoldAsync(stopping.Token);
            Phase(FleetPhase.Running);
            return opened;
        }
        catch (Exception e)
        {
            lock (_gate)
            {
                _fault = e.Message;
            }

            Say("FAULT: " + e.Message);
            Phase(FleetPhase.Faulted);
            await TearDownAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Closes every session and releases the swarm.</summary>
    public async Task StopAsync()
    {
        lock (_gate)
        {
            if (_phase is FleetPhase.Idle or FleetPhase.Stopping)
            {
                return;
            }

            _phase = FleetPhase.Stopping;
        }

        Notify();

        CancellationTokenSource stopping;
        Task run;
        lock (_gate)
        {
            stopping = _stopping;
            run = _run;
        }

        if (stopping != null)
        {
            await stopping.CancelAsync().ConfigureAwait(false);
        }

        if (run != null)
        {
            try
            {
                await run.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // ANY exception, not just cancellation. A faulted hold task used to propagate out of here and skip the teardown
                // below, which leaked the swarm -- a thousand open sockets -- and left the accounts file unflushed. The run's
                // failure is already recorded in _fault; what matters at this point is that everything is released.
            }
        }

        await TearDownAsync().ConfigureAwait(false);

        string fault;
        lock (_gate)
        {
            fault = _fault;

            // Disposed here rather than only on the next Start: a panel that runs twenty fleets in an afternoon would otherwise
            // hold twenty token sources, each with its registrations, for the life of the process.
            _stopping?.Dispose();
            _stopping = null;
            _run = null;
        }

        Phase(fault == null ? FleetPhase.Idle : FleetPhase.Faulted);
    }

    /// <summary>Stops the run and releases everything.</summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stopping?.Dispose();
    }

    /// <summary>Signs up or logs in the population when the run needs accounts.</summary>
    /// <param name="settings">The run's configuration.</param>
    /// <param name="endpoint">The game endpoint.</param>
    /// <param name="ct">Cancels the pass.</param>
    private async Task ProvisionAsync(FleetSettings settings, Uri endpoint, CancellationToken ct)
    {
        if (!settings.IsPlayer)
        {
            // A god session is anonymous and read-only, so it needs no account at all — which is also why a panel can drive a
            // god fleet with no key in the environment.
            //
            // <b>And the store is dropped, which is not housekeeping.</b> This runner is a singleton the panel reuses, so a god run
            // started after a player run inherited the previous store: BuildOptions read it, TokenFor handed out real bearer
            // tokens, and the sessions were authenticated players while the page said "anonymous camera" and the teardown flushed
            // a file the run had never used.
            lock (_gate)
            {
                _accounts = null;
                _provisioned = settings.Count;
            }

            Say("god sessions are anonymous: no accounts needed");
            return;
        }

        if (string.IsNullOrWhiteSpace(settings.AccountsFile))
        {
            throw new InvalidOperationException("a player fleet needs an accounts file: the server refuses a player session with no token (close 4102)");
        }

        var authBase = BotProvisioner.AuthBaseFrom(endpoint)
            ?? throw new InvalidOperationException($"{endpoint} has no HTTP side to sign up against");

        var key = BotCredentials.ReadKeyOrThrow();
        var accounts = new BotAccountStore(settings.AccountsFile, settings.Count);
        lock (_gate)
        {
            _accounts = accounts;
        }

        Say($"accounts: {accounts.FilePath} ({accounts.UsableTokens(BotProvisioner.RefreshWindowSeconds)} of {settings.Count} tokens usable)");
        using var provisioner = new BotProvisioner(authBase, accounts, key, settings.SignupConcurrency);
        var report = await provisioner.EnsureAsync(
            settings.Count,
            (done, total) =>
            {
                lock (_gate)
                {
                    _provisioned = done;
                }

                Notify();
                _ = total;
            },
            ct).ConfigureAwait(false);

        Say($"accounts ready in {report.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s: "
            + $"{report.Created} created, {report.LoggedIn} logged in, {report.Reused} reused, {report.Failed} failed");

        if (!report.Complete)
        {
            throw new InvalidOperationException($"{report.Failed} account(s) could not be provisioned; first failure: {report.FirstFailure}");
        }

        lock (_gate)
        {
            // The accounts that are actually READY, not the population. Assigning the population here reported a fleet as fully
            // provisioned on a pass that had failures -- and since the throw above only fires when there are failures, the two
            // lines together made the number unfalsifiable.
            _provisioned = report.Created + report.LoggedIn + report.Reused;
        }
    }

    /// <summary>Turns the editable settings into the swarm's immutable options.</summary>
    /// <param name="settings">The run's configuration.</param>
    /// <param name="endpoint">The game endpoint.</param>
    /// <returns>The options.</returns>
    private BotSwarmOptions BuildOptions(FleetSettings settings, Uri endpoint)
    {
        var accounts = _accounts;
        return new BotSwarmOptions
        {
            Endpoint = endpoint,
            Count = settings.Count,
            Kind = settings.Kind,
            TickHz = settings.TickHz,
            ConnectRatePerSecond = settings.ConnectRatePerSecond,
            RegionRadiusM = settings.RegionRadiusM,
            TokenFor = accounts == null ? null : index => accounts[index]?.Token,
            ResumeFor = accounts == null ? null : accounts.Resume,
            OnResumeToken = accounts == null
                ? null
                : (index, token) =>
                {
                    accounts.PutResume(index, token);
                    accounts.FlushIfDue();
                },
        };
    }

    /// <summary>Holds the run open until it is cancelled, raising <see cref="Changed"/> on a cadence a panel can render at.</summary>
    /// <param name="ct">Cancels the hold.</param>
    private async Task HoldAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Notify();
        }
    }

    /// <summary>Releases the swarm, the accounts file and the shaping.</summary>
    private async Task TearDownAsync()
    {
        BotSwarm swarm;
        lock (_gate)
        {
            swarm = _swarm;
            _swarm = null;
        }

        if (swarm != null)
        {
            await swarm.DisposeAsync().ConfigureAwait(false);
        }

        BotAccountStore accounts;
        string shaping;
        string device;
        lock (_gate)
        {
            accounts = _accounts;
            shaping = _shaping;
            device = _shapedDevice;

            // Stops the clock the snapshot reads, so a finished run's duration stops growing on the page.
            if (_startedAt != 0 && _stoppedAt == 0)
            {
                _stoppedAt = Stopwatch.GetTimestamp();
            }
        }

        // Unconditional, because FlushIfDue leaves up to a threshold's worth of resume tokens unwritten and those are exactly what
        // a restarted fleet needs. Outside the lock: it writes a file.
        accounts?.Flush();

        if (shaping != null)
        {
            await NetemShaper.ClearAsync(device, CancellationToken.None).ConfigureAwait(false);
            lock (_gate)
            {
                _shaping = null;
                _shapedDevice = null;
            }
        }
    }

    /// <summary>Moves the phase and tells the panel.</summary>
    /// <param name="phase">The new phase.</param>
    private void Phase(FleetPhase phase)
    {
        lock (_gate)
        {
            _phase = phase;
        }

        // Forced: a phase change is what enables and disables the panel's buttons, so it is the one notification a renderer
        // cannot be allowed to miss.
        Notify(force: true);
    }

    /// <summary>Appends a log line, keeping the log bounded.</summary>
    /// <param name="line">The line.</param>
    private void Say(string line)
    {
        lock (_gate)
        {
            _log.Add(DateTime.UtcNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line);
            if (_log.Count > LogCapacity)
            {
                _log.RemoveRange(0, _log.Count - LogCapacity);
            }
        }

        // Forced: a log line is written a handful of times per run and is the account of what happened, so none is dropped.
        Notify(force: true);
    }

    /// <summary>
    /// Raises <see cref="Changed"/>, at most a few times a second, without letting a subscriber's exception reach the run.
    /// </summary>
    /// <remarks>
    /// <b>Rate-limited, because the callers are per-item.</b> Provisioning notifies per account and the ramp notifies per batch,
    /// so a thousand-bot fleet raised a thousand notifications — and each one costs the panel a full <see cref="Snapshot"/> (four
    /// walks of the population plus a dictionary copy) and a copy of the five-hundred-line log. The generator must not be the
    /// expensive thing in its own measurement.
    /// <para>
    /// A dropped notification is harmless: the panel's own one-second timer and the next phase change both re-read, so the worst a
    /// skip costs is up to a quarter of a second of staleness. Phase changes are therefore NOT rate-limited — those are the ones a
    /// renderer must not miss.
    /// </para>
    /// </remarks>
    private void Notify(bool force = false)
    {
        if (!force)
        {
            var now = Environment.TickCount64;
            if (now - Volatile.Read(ref _lastNotify) < NotifyIntervalMs)
            {
                return;
            }

            Volatile.Write(ref _lastNotify, now);
        }

        try
        {
            Changed?.Invoke();
        }
        catch (Exception)
        {
            // A renderer that threw must not take the fleet down with it. This is a load generator; the load is the product.
        }
    }
}

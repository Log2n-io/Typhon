using System;
using System.Collections.Generic;
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
    public IReadOnlyDictionary<ushort, int> Disconnects => _disconnects;

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
    public int Faults { get; private set; }

    /// <summary>How many times the shared timer has fired.</summary>
    public long Ticks { get; private set; }

    private readonly Dictionary<ushort, int> _disconnects = [];

    /// <summary>Connects every bot and starts the shared driver.</summary>
    /// <param name="ct">Cancels the start.</param>
    /// <returns>How many sessions opened.</returns>
    public async Task<int> StartAsync(CancellationToken ct = default)
    {
        for (var i = 0; i < _options.Count; i++)
        {
            var bot = new Bot(i, _options);
            bot.Client.Disconnected += (code, _) => Note(code);
            bot.Client.Fault += _ => Faults++;

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

    private void Note(ushort code) => _disconnects[code] = _disconnects.GetValueOrDefault(code) + 1;

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

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Client;
using Typhon.Protocol;

namespace SwgTatooine.Bots;

/// <summary>
/// Hostile clients for AC-17's live half (design/Subscriptions/11 § 4.1): connections that handshake like any player, then send mutated messages as fast as
/// their rate allows, reconnecting whenever the server closes them. Run beside a normal swarm, it answers the one AC-17 question an in-process fuzzer
/// cannot — does hostile input on the transport threads move the TICK's p99 — so the swarm's own SWEEP line is the measurement, and this reports only what
/// it sent and how the server answered.
/// </summary>
/// <remarks>
/// <b>A rate of zero is the control arm:</b> the same connections, handshakes and 4 Hz pings, no mutation. The difference between the two arms is then the
/// hostile traffic alone, not "fifty more sessions".
/// </remarks>
public sealed class Fuzzer
{
    private readonly Uri _endpoint;
    private readonly int _clients;
    private readonly int _ratePerClient;
    private readonly string _mode;
    private readonly ConcurrentDictionary<ushort, int> _closes = new();
    private long _sent;
    private long _malformed;
    private long _connects;
    private long _sendFailures;

    /// <summary>Creates the fuzzer.</summary>
    /// <param name="endpoint">The server's WebSocket endpoint.</param>
    /// <param name="clients">How many hostile connections to keep open.</param>
    /// <param name="ratePerClient">Messages a second per connection; 0 for the control arm (pings only).</param>
    /// <param name="mode">
    /// <c>mixed</c> (40 % mutated), <c>valid</c> (no mutation: budget and rate refusals only), <c>churn</c> (connect, close, reconnect — no traffic), or
    /// <c>mixed-slow</c> (mixed, but a closed connection waits a second before reconnecting, so the load is the messages rather than the reconnects).
    /// </param>
    public Fuzzer(Uri endpoint, int clients, int ratePerClient, string mode = "mixed")
    {
        _endpoint = endpoint;
        _clients = clients;
        _ratePerClient = ratePerClient;
        _mode = mode;
    }

    /// <summary>Runs every connection until <paramref name="ct"/> fires, then prints one greppable line.</summary>
    /// <param name="ct">Stops the run.</param>
    public async Task RunAsync(CancellationToken ct)
    {
        var tasks = new List<Task>(_clients);
        for (var i = 0; i < _clients; i++)
        {
            var seed = 1_000 + i;
            tasks.Add(Task.Run(() => ClientLoopAsync(seed, ct), CancellationToken.None));
        }

        // Totals every 5 s as well as at the end: a harness that stops the fuzzer when its swarm finishes still has the last line.
        var all = Task.WhenAll(tasks);
        while (!all.IsCompleted)
        {
            await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None)).ConfigureAwait(false);
            Report();
        }
    }

    private void Report()
    {
        var closes = string.Join(",", SortedCloses());
        var cpu = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds;
        Console.WriteLine($"FUZZ mode={_mode} cpuS={cpu:F1} clients={_clients} rate={_ratePerClient} sent={Interlocked.Read(ref _sent)} malformed={Interlocked.Read(ref _malformed)} "
            + $"connects={Interlocked.Read(ref _connects)} sendFailures={Interlocked.Read(ref _sendFailures)} closes={closes}");
    }

    private IEnumerable<string> SortedCloses()
    {
        var keys = new List<ushort>(_closes.Keys);
        keys.Sort();
        foreach (var key in keys)
        {
            yield return $"{key}:{_closes[key]}";
        }
    }

    private async Task ClientLoopAsync(int seed, CancellationToken ct)
    {
        var random = new Random(seed);
        while (!ct.IsCancellationRequested)
        {
            IClientTransport transport = null;
            var client = new TyphonClient(
                new ClientOptions { Endpoint = _endpoint, Kind = "player", PingHz = 0, Reconnect = false, SegmentHistory = 1 },
                () => transport = new WebSocketClientTransport(_endpoint));
            client.Disconnected += (code, _) => _closes.AddOrUpdate(code, 1, (_, n) => n + 1);
            try
            {
                await client.ConnectAsync(ct).ConfigureAwait(false);
                Interlocked.Increment(ref _connects);
                if (_mode != "churn")
                {
                    await SendLoopAsync(client, transport, random, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _sendFailures);
            }
            finally
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }

            if (_mode == "mixed-slow" && !ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }

    private async Task SendLoopAsync(TyphonClient client, IClientTransport transport, Random random, CancellationToken ct)
    {
        var region = client.Plan.CommandByName(BuiltInCommands.ClientRegion);
        ushort seq = 0;
        var interval = _ratePerClient > 0 ? TimeSpan.FromSeconds(1.0 / _ratePerClient) : TimeSpan.FromMilliseconds(250);
        var next = DateTime.UtcNow;
        while (!ct.IsCancellationRequested && client.IsConnected)
        {
            byte[] message;
            if (_ratePerClient == 0)
            {
                message = Ping(client);
            }
            else
            {
                var roll = random.Next(100);
                if (roll < 20)
                {
                    message = Ping(client);
                }
                else
                {
                    message = region == null ? Ping(client) : Region(region, ++seq, random);
                    if (roll >= 60 && _mode != "valid")
                    {
                        message = Mutate(message, random);
                        Interlocked.Increment(ref _malformed);
                    }
                }
            }

            await transport.SendAsync(message, ct).ConfigureAwait(false);
            Interlocked.Increment(ref _sent);

            // A deadline, not a delay per message: Task.Delay rounds up to the timer's resolution, which would cap a client at ~64 messages a second.
            next += interval;
            var wait = next - DateTime.UtcNow;
            if (wait > TimeSpan.FromMilliseconds(15))
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            else if (wait < -TimeSpan.FromSeconds(1))
            {
                next = DateTime.UtcNow;
            }
        }
    }

    private static byte[] Ping(TyphonClient client) => Encode(new PingMessage((uint)Environment.TickCount, client.LastAppliedTick).Write);

    private static byte[] Region(MessagePlan plan, ushort seq, Random random)
    {
        var x = (random.NextDouble() * 4000) - 2000;
        var y = (random.NextDouble() * 4000) - 2000;
        var values = new RecordValues
        {
            // list<pos3> (typhon.3): a flat quad at z 0.
            [BuiltInCommands.RegionVerticesField] = FieldValue.Of(x, y, 0d, x + 200, y, 0d, x + 200, y + 200, 0d, x, y + 200, 0d),
            [BuiltInCommands.RegionAltitudeField] = FieldValue.Of(120.0),
            [BuiltInCommands.RegionBudgetField] = FieldValue.Of(64),
        };

        var buffer = new byte[512];
        var writer = new WireWriter(buffer);
        CommandsMessage.Write(ref writer, 1, [(plan, seq, values)]);
        return writer.Written.ToArray();
    }

    /// <summary>The in-process fuzzer's mutations (ClientInputFuzzTests), in small: flips, truncation, garbage, a foreign type byte.</summary>
    private static byte[] Mutate(byte[] valid, Random random)
    {
        var bytes = (byte[])valid.Clone();
        switch (random.Next(5))
        {
            case 0:
                for (var i = random.Next(1, 5); i > 0; i--)
                {
                    bytes[random.Next(bytes.Length)] ^= (byte)(1 << random.Next(8));
                }

                return bytes;
            case 1:
                return bytes[..random.Next(1, Math.Max(2, bytes.Length))];
            case 2:
                var longer = new byte[bytes.Length + random.Next(1, 64)];
                bytes.CopyTo(longer, 0);
                random.NextBytes(longer.AsSpan(bytes.Length));
                return longer;
            case 3:
                bytes[0] = (byte)random.Next(256);
                return bytes;
            default:
                var garbage = new byte[random.Next(1, 96)];
                random.NextBytes(garbage);
                return garbage;
        }
    }

    private delegate void Writer(ref WireWriter writer);

    private static byte[] Encode(Writer write)
    {
        var buffer = new byte[64];
        var writer = new WireWriter(buffer);
        write(ref writer);
        return writer.Written.ToArray();
    }
}

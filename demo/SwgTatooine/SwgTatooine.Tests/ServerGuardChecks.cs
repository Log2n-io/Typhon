using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using SwgTatooine.Server;
using SwgTatooine.Server.Accounts;
using Typhon.Engine;

namespace SwgTatooine.Tests;

/// <summary>
/// The served world's guard (<see cref="ServerGuard"/>, #1532): the gate on database requests, shedding on the engine's storage health, the circuit breaker,
/// a stopping server's refusals and the tick watchdog — and, against a live world, a storm of inventory reads that the gate absorbs while the tick goes on.
/// </summary>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
internal sealed class ServerGuardChecks : ServedChecks
{
    private static ServerGuard Guard(int width = 1, int tripCount = 3, Func<long> now = null)
        => new(width, TimeSpan.FromMilliseconds(50), tripCount, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15), now)
        {
            StopDeadline = TimeSpan.FromHours(1),
        };

    private static async Task<(int Status, string RetryAfter, bool Ran)> Send(ServerGuard guard, string path, RequestDelegate handler = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        var ran = false;
        await guard.Handle(context, c =>
        {
            ran = true;
            return handler?.Invoke(c) ?? Task.CompletedTask;
        });
        return (context.Response.StatusCode, context.Response.Headers.RetryAfter.ToString(), ran);
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task AFullGate_Answers503WithRetryAfter_AndRunsNothing_AndAMemoryRouteIsNeverGated()
    {
        var guard = Guard();
        var held = new TaskCompletionSource();
        var holding = Send(guard, "/swg/inventory", _ => held.Task);

        var refused = await Send(guard, "/swg/wallet");
        var memory = await Send(guard, "/swg/stats");
        held.SetResult();
        var first = await holding;
        var after = await Send(guard, "/swg/wallet");
        Assert.Multiple(() =>
        {
            Assert.That(refused, Is.EqualTo((503, "1", false)), "a database read past a full gate");
            Assert.That(guard.Refused, Is.EqualTo(1));
            Assert.That(memory.Ran, Is.True, "a route answering from memory was gated");
            Assert.That(first.Ran && after.Ran, Is.True, "the gate did not give its place back");
        });
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task OverloadedStorage_ShedsLoginsSessionsAndDatabaseReads_AndServesTheRest_UntilItRecovers()
    {
        var guard = Guard(width: 4);
        guard.OnStorageHealth(StorageHealthState.Overloaded);
        var login = await Send(guard, "/auth/login");
        var session = await Send(guard, "/ws");
        var read = await Send(guard, "/swg/inventory");
        var stats = await Send(guard, "/typhon/stats.json");
        var memory = await Send(guard, "/swg/market");
        var client = await Send(guard, "/index.html");
        guard.OnStorageHealth(StorageHealthState.Starved);
        var starved = await Send(guard, "/swg/missions");
        guard.OnStorageHealth(StorageHealthState.Pressured);
        var recovered = await Send(guard, "/swg/inventory");
        Assert.Multiple(() =>
        {
            Assert.That(login, Is.EqualTo((503, "2", false)), "a login while Overloaded");
            Assert.That(session, Is.EqualTo((503, "2", false)), "a new session while Overloaded");
            Assert.That(read, Is.EqualTo((503, "2", false)), "a database read while Overloaded");
            Assert.That(starved.Ran, Is.False, "a database read while Starved");
            Assert.That(stats.Ran && memory.Ran && client.Ran, Is.True, "a request that touches no database was shed");
            Assert.That(recovered.Ran, Is.True, "still shedding once the storage recovered");
            Assert.That(guard.Shed, Is.EqualTo(4));
        });
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task EngineFailures_OpenTheBreaker_WhichFailsFast_AndClosesAfterItsCoolDown()
    {
        long now = 0;
        var guard = Guard(width: 4, tripCount: 3, now: () => now);
        RequestDelegate failing = _ => throw new TimeoutException("a lock timed out");

        var answers = new (int, string, bool)[3];
        for (var i = 0; i < 3; i++)
        {
            answers[i] = await Send(guard, "/swg/inventory", failing);
            now += 1000;
        }

        var whileOpen = await Send(guard, "/swg/inventory");
        var login = await Send(guard, "/auth/login");
        now += 15_000;
        var afterCoolDown = await Send(guard, "/swg/inventory");

        Assert.Multiple(() =>
        {
            Assert.That(answers.All(a => a == (503, "2", true)), Is.True, "an engine failure was not answered 503");
            Assert.That(guard.Failed, Is.EqualTo(3));
            Assert.That(guard.Trips, Is.EqualTo(1), "three failures in ten seconds did not open the breaker");
            Assert.That(whileOpen, Is.EqualTo((503, "2", false)), "a request ran through an open breaker");
            Assert.That(login.Ran, Is.False, "a login through an open breaker");
            Assert.That(afterCoolDown.Ran, Is.True, "the breaker did not close after its cool-down");
        });
    }

    [Test]
    [CancelAfter(10_000)]
    public void FailuresSpreadPastTheWindow_DoNotOpenIt_AndANonEngineFailureIsNotCaught()
    {
        long now = 0;
        var guard = Guard(width: 4, tripCount: 3, now: () => now);
        for (var i = 0; i < 6; i++)
        {
            guard.RecordEngineFailure(new TimeoutException());
            now += 6_000;
        }

        Assert.Multiple(() =>
        {
            Assert.That(guard.Trips, Is.Zero, "failures six seconds apart opened a ten-second breaker of three");
            Assert.That(ServerGuard.IsEngineFailure(new LockTimeoutException("x", TimeSpan.FromSeconds(1))), Is.True);
            Assert.That(ServerGuard.IsEngineFailure(new InvalidOperationException("a", new TimeoutException())), Is.True, "an engine failure, wrapped");
            Assert.That(ServerGuard.IsEngineFailure(new ArgumentException()), Is.False);
            Assert.ThrowsAsync<ArgumentException>(() => Send(guard, "/swg/inventory", _ => throw new ArgumentException("a bug, not the engine")));
        });
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task AStoppingServer_RefusesAllButHealthAndStats_StopsItsHostOnce_AndSaysWhy()
    {
        var guard = Guard(width: 4);
        var stops = 0;
        guard.Attach(() => stops++);
        guard.Stop("tick 7 aborted: SystemException in 'Combat'");
        guard.Stop("a second reason");

        var asked = Guard(width: 4);
        asked.Stop("asked to stop");

        var client = await Send(guard, "/index.html");
        var memory = await Send(guard, "/swg/stats");
        var health = await Send(guard, "/healthz");
        var stats = await Send(guard, "/typhon/stats.json");
        Assert.Multiple(() =>
        {
            Assert.That(stops, Is.EqualTo(1), "the host was not stopped, or stopped twice");
            Assert.That(guard.StopReason, Does.StartWith("tick 7 aborted"), "the first reason is the one kept");
            Assert.That(guard.Faulted, Is.True);
            Assert.That(asked.Faulted, Is.False, "a stop asked from outside is a fault");
            Assert.That(client, Is.EqualTo((503, "5", false)));
            Assert.That(memory.Ran, Is.False);
            Assert.That(health.Ran && stats.Ran, Is.True, "a stopping server hid its health");
        });
    }

    [Test]
    [CancelAfter(20_000)]
    public void ATickThatStandsStill_StopsTheServer_AndOneThatMovesDoesNot()
    {
        var still = Guard();
        still.WatchTick(() => 42, TimeSpan.FromSeconds(2), TimeSpan.FromHours(1));
        long moving = 0;
        var alive = Guard();
        alive.WatchTick(() => Interlocked.Increment(ref moving), TimeSpan.FromSeconds(2), TimeSpan.FromHours(1));

        var deadline = Environment.TickCount64 + 10_000;
        while (still.StopReason == null && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(50);
        }

        Assert.Multiple(() =>
        {
            Assert.That(still.StopReason, Does.Contain("has not advanced past 42"), "a stalled tick did not stop the server");
            Assert.That(still.Faulted, Is.True);
            Assert.That(alive.StopReason, Is.Null, "a moving tick stopped the server");
        });
        alive.Stop("asked to stop");
    }

    /// <summary>
    /// Item 8 of #1532's list, at the HTTP layer: a live served world, 300 inventory reads at once through a real Kestrel host behind the guard. Every one
    /// is answered 200 or 503 — never an engine exception leaking out as a 500 — some are served, and the tick keeps advancing throughout.
    /// </summary>
    [Test]
    [CancelAfter(180_000)]
    public async Task AStormOfInventoryReads_IsAbsorbedByTheGate_AndTheTickGoesOn()
    {
        var config = Worlds.Served(Dir);
        config.WorkerCount = 1;
        using var sim = new TatooineSim(config);
        sim.Initialize();
        Harness = new SessionHarness(sim);
        var account = TestAccounts.For(sim.Dbe, 0x9A01);
        Harness.Connect(TatooineReplication.PlayerKind, account);
        SessionHarness.Until(() => CharacterStore.TryFind(sim.Dbe, account, out _, out _), "the character");

        var guard = new ServerGuard(2, TimeSpan.FromSeconds(2), 20, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15)) { StopDeadline = TimeSpan.FromHours(1) };
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Error);
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.Use(guard.Handle);
        app.MapItems(sim.Dbe);
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses.First();
            using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(60) };
            var token = "Bearer " + AuthToken.Mint(account);
            var tickBefore = sim.Runtime.CurrentTickNumber;
            var statuses = new ConcurrentBag<HttpStatusCode>();
            await Task.WhenAll(Enumerable.Range(0, 300).Select(async _ =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/swg/inventory");
                request.Headers.TryAddWithoutValidation("Authorization", token);
                using var response = await client.SendAsync(request);
                statuses.Add(response.StatusCode);
            }));
            Harness.Ticks(3);
            var tickAfter = sim.Runtime.CurrentTickNumber;

            var ok = statuses.Count(s => s == HttpStatusCode.OK);
            var busy = statuses.Count(s => s == HttpStatusCode.ServiceUnavailable);
            Assert.Multiple(() =>
            {
                Assert.That(ok + busy, Is.EqualTo(300), $"answers other than 200 or 503: {string.Join(", ", statuses.Where(s => s != HttpStatusCode.OK && s != HttpStatusCode.ServiceUnavailable).Distinct())}");
                Assert.That(ok, Is.GreaterThan(0), "the gate served nothing");
                Assert.That(guard.Failed, Is.Zero, "an engine failure under the storm");
                Assert.That(tickAfter, Is.GreaterThan(tickBefore), "the tick stood still under the storm");
            });
        }
        finally
        {
            await app.StopAsync();
        }
    }
}

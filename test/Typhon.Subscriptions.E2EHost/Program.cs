using System;
using System.Globalization;
using System.IO;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.Engine;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace Typhon.Subscriptions.E2EHost;

/// <summary>
/// Serves the end-to-end world over TCP until its standard input closes.
/// </summary>
/// <remarks>
/// <para>
/// <b>The contract with a driver is two lines of text.</b> The host prints <c>PORT &lt;n&gt;</c> once it accepts connections, and stops when its standard
/// input reaches end of file — so a driver that dies takes the host with it, and nothing has to find the process to kill it.
/// </para>
/// <para>
/// Usage: <c>Typhon.Subscriptions.E2EHost [--port N] [--hz N]</c>. Port 0 (the default) binds an ephemeral one, which is what a test wants: two runs on one
/// machine never collide.
/// </para>
/// </remarks>
public static class Program
{
    private const string Kind = "probe";
    private const string Profile = "world";
    private const float WorldExtentM = 8192f;
    private const int MoverCount = 12;
    private const int RockCount = 4;
    private const double MaxSpeedMps = 20.0;

    public static int Main(string[] args)
    {
        var port = 0;
        var hz = 20;
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--port":
                    port = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
                    break;
                case "--hz":
                    hz = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
                    break;
                default:
                    Console.Error.WriteLine($"unknown option {args[i]}");
                    return 2;
            }
        }

        var directory = Path.Combine(Path.GetTempPath(), $"typhon-e2e-{Environment.ProcessId}");
        Directory.CreateDirectory(directory);
        try
        {
            Serve(directory, port, hz);
            return 0;
        }
        finally
        {
            // The database is scratch: a run that left it behind would be the next run's mystery.
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static void Serve(string directory, int port, int hz)
    {
        var services = new ServiceCollection();
        services
            .AddLogging(cfg => cfg.SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(opt =>
            {
                opt.DatabaseName = "E2EHost";
                opt.DatabaseDirectory = directory;
                opt.DatabaseCacheSize = 64UL * 1024 * 1024;
            })
            .AddScopedDatabaseEngine(opt => opt.Wal = new WalWriterOptions());

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<E2eBounds>();
        dbe.RegisterComponentFromAccessor<E2eState>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
            worldMin: new Vector2(-WorldExtentM, -WorldExtentM),
            worldMax: new Vector2(WorldExtentM, WorldExtentM),
            cellSize: 256f));
        dbe.InitializeArchetypes();
        Populate(dbe);

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("E2E");
            dag.CallbackSystem("Sessions", ctx => BindProfiles(ctx));
            dag.CallbackSystem("Move", ctx => Move(ctx));
            dag.CallbackSystem("Events", ctx => Events(ctx));
        }, new RuntimeOptions
        {
            WorkerCount = 1,
            BaseTickRate = hz,
            // A probe sends one small command per run; 16 KiB/s is ample and bounds a misbehaving client all the same.
            Subscriptions = new SubscriptionsOptions { ReplicationCellM = 2d * WorldExtentM / 48d, IngressBytesPerSecond = 16 * 1024 },
        });

        Declare(runtime.Subscriptions);
        runtime.Start();
        var transport = new TcpSubscriptionTransport(new TcpSubscriptionOptions { Port = port });
        runtime.StartSubscriptionTransport(transport);
        try
        {
            Console.Out.WriteLine($"PORT {transport.BoundEndPoint.Port.ToString(CultureInfo.InvariantCulture)}");
            Console.Out.Flush();
            // Served until the driver closes our standard input.
            while (Console.In.ReadLine() != null)
            {
            }
        }
        finally
        {
            transport.StopAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            runtime.Shutdown();
        }
    }

    private static void Declare(SubscriptionsRegistry subs)
    {
        subs.Sessions.Kinds(Kind);
        subs.Archetype<E2eMover>(a => a
            .Motion(E2eMover.Bounds, m => m.Tolerance(0.05).Teleport(MaxSpeedMps))
            .OnEnter(E2eMover.State, x => x.Template, Codec.U8, name: "template")
            .Field(E2eMover.State, x => x.Mode, Codec.Enum<E2eMode>(bits: 3), name: "mode")
            .Field(E2eMover.State, x => x.Alerted, Codec.Bool.Saturate(), name: "alerted")
            .Field(E2eMover.State, x => x.Level, Codec.U16, name: "level", group: "vitals")
            .Fraction(E2eMover.State, x => x.Health, x => x.MaxHealth, bits: 8, name: "hp", group: "vitals")
            // The exact wire (W32, W33): 64-bit integers past 2⁵³, a double, a point — what a client decodes without a double in between.
            .Field(E2eMover.State, x => x.Credits, Codec.Exact, name: "credits", group: "vitals")
            .Field(E2eMover.State, x => x.Debt, Codec.VarInt64, name: "debt", group: "vitals")
            .Field(E2eMover.State, x => x.Rate, Codec.Exact, name: "rate", group: "vitals")
            .Field(E2eMover.State, x => x.Aim, Codec.Exact, name: "aim"));
        subs.Static<E2eRock>(a => a
            .Position(E2eRock.Bounds)
            .Field(E2eRock.State, x => x.Template, Codec.U8, name: "kind"));
        subs.Event<E2ePulse>(e => e.Broadcast().Field(p => p.Seq, Codec.U32).Field(p => p.Kind, Codec.U16).Field(p => p.Stamp, Codec.VarUInt64));
        subs.Event<E2eEchoed>(e => e.Broadcast().Field(p => p.Value, Codec.U32).Field(p => p.Code, Codec.U16).Field(p => p.Token, Codec.U64));
        subs.Command<E2eEcho>(c => c.Field(p => p.Value, Codec.U32).Field(p => p.Code, Codec.U16).Field(p => p.Token, Codec.U64));
        subs.Profile(Profile, p => p.World().Of<E2eMover>().Of<E2eRock>());
    }

    private static void Populate(DatabaseEngine dbe)
    {
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < MoverCount; i++)
            {
                var bounds = new E2eBounds { Bounds = new AABB2F { MinX = i * 9f, MinY = 10f, MaxX = (i * 9f) + 1f, MaxY = 11f }, Speed = 1f };
                var state = new E2eState
                {
                    Template = (byte)(i + 1), Mode = (E2eMode)(i % 4), Alerted = (byte)(i & 1), Level = (ushort)(10 + i), Health = 8, MaxHealth = 10,
                    Credits = (1UL << 53) + 1 + (ulong)i, Debt = -(1L << 53) - i, Rate = 0.1 * (i + 1),
                    Aim = new Point3F { X = i, Y = -i, Z = 0.5f },
                };
                tx.Spawn<E2eMover>(E2eMover.Bounds.Set(in bounds), E2eMover.State.Set(in state));
            }

            for (var i = 0; i < RockCount; i++)
            {
                var bounds = new E2eBounds { Bounds = new AABB2F { MinX = -100f - (i * 30f), MinY = -50f, MaxX = -99f - (i * 30f), MaxY = -49f } };
                var state = new E2eState { Template = (byte)(200 + i) };
                tx.Spawn<E2eRock>(E2eRock.Bounds.Set(in bounds), E2eRock.State.Set(in state));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
    }

    private static void BindProfiles(in TickContext ctx)
    {
        var subs = ctx.Subscriptions;
        if (subs == null)
        {
            return;
        }

        foreach (ref readonly var e in subs.SessionEvents)
        {
            if (e.Kind == SessionEventKind.Opened)
            {
                subs.Session(e.Session).Profile(Profile);
            }
        }
    }

    // NOT a straight line: constant velocity is the one trajectory a motion segment predicts exactly, and the server would rightly send nothing.
    private static void Move(in TickContext ctx)
    {
        var tx = ctx.Transaction;
        if (tx == null)
        {
            return;
        }

        var tick = ctx.TickNumber;
        var phase = tick * 0.37f;
        var subs = ctx.Subscriptions;
        // NOT disposed: the accessor belongs to the tick's transaction.
        var accessor = tx.For<E2eMover>();
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
            var occupancy = cluster.OccupancyBits;
#pragma warning disable TYPHON009
            var bounds = cluster.GetSpan(E2eMover.Bounds);
#pragma warning restore TYPHON009
            var state = cluster.GetSpan(E2eMover.State);
            while (occupancy != 0)
            {
                var slot = BitOperations.TrailingZeroCount(occupancy);
                occupancy &= occupancy - 1;
                var current = bounds[slot];
                var width = current.Bounds.MaxX - current.Bounds.MinX;
                current.Bounds.MinX = (slot * 9f) + (8f * MathF.Sin(phase + slot));
                current.Bounds.MaxX = current.Bounds.MinX + width;
                cluster.WriteSpatial(E2eMover.Bounds, slot, current);

                // Every few ticks a third of the movers change their state — the default group (mode, alerted) on one beat, the vitals group
                // (level, hp) on another — so a client applies state records, not only motion and enters. A span write is not pushed by the
                // engine: the system marks the slot it wrote.
                if (subs != null && tick % 3 == slot % 3)
                {
                    ref var s = ref state[slot];
                    if (tick % 7 == 0)
                    {
                        s.Mode = (E2eMode)(((int)s.Mode + 1) & 3);
                        s.Alerted ^= 1;
                        s.Aim = new Point3F { X = s.Aim.X + 0.25f, Y = s.Aim.Y, Z = -s.Aim.Z };
                    }

                    if (tick % 5 == 0)
                    {
                        s.Level = (ushort)(s.Level + 1);
                        s.Health = (int)((tick / 5 + slot) % (s.MaxHealth + 1));
                        s.Credits += 1UL << 40;
                        s.Debt -= 1;
                        s.Rate *= 1.5;
                    }

                    if (tick % 7 == 0 || tick % 5 == 0)
                    {
                        subs.Replicate(in cluster, slot);
                    }
                }
            }
        }
    }

    private static void Events(in TickContext ctx)
    {
        var subs = ctx.Subscriptions;
        if (subs == null)
        {
            return;
        }

        if (ctx.TickNumber % 5 == 0)
        {
            subs.Emit(new E2ePulse { Seq = (uint)ctx.TickNumber, Kind = (ushort)(ctx.TickNumber % 7), Stamp = ulong.MaxValue - (ulong)ctx.TickNumber });
        }

        foreach (ref readonly var command in subs.Commands<E2eEcho>())
        {
            subs.Emit(new E2eEchoed { Value = command.Value.Value, Code = command.Value.Code, Token = command.Value.Token });
        }
    }
}

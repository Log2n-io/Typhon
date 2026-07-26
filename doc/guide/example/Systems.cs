// Systems for the World-Shard guide example — the tick-loop logic that turns the schema into a living planet shard.
// Together with Program.cs this is the pair the `typhon new` scaffold emits as its starter template.
//
// The whole point is the SPLIT between the two tiers:
//   • The HOT tier runs lock-free in parallel over the SingleVersion components (movement, spatial, HAM, AI). Each
//     worker writes its own slice through ctx.Accessor — no locks, no MVCC, no commit. This is where Typhon flies.
//   • The ECONOMY is a single Versioned transfer at EVENT cadence (a few trades per tick), settled serially through
//     ctx.Transaction. That is the ONLY Versioned write in the loop — reaching for MVCC on per-tick state is the
//     mistake this sample avoids.
//
//   SpawnSystem      — CallbackSystem: non-entity work (periodically spawn a fresh character).
//   MoveSystem       — parallel QuerySystem: integrate velocity into position (a lock-free SingleVersion write).
//   BoundsSyncSystem — parallel cluster-native QuerySystem: keep the spatial Bounds coherent after movement.
//   RegenSystem      — parallel QuerySystem: regenerate the HAM pools each tick (lock-free SingleVersion).
//   WanderSystem     — parallel QuerySystem: refresh the Transient AI intent and steer velocity toward it.
//   TradeSystem      — CallbackSystem: settle a few credit transfers per tick, each an atomic Versioned transaction.

using System;
using System.Numerics;
using Typhon.Engine;
using Typhon.Samples.Swg.Shard;
using Typhon.Schema.Definition;

namespace SwgGuide;

/// <summary>Non-entity work: every 30 ticks, spawn a fresh character into the shard. A CallbackSystem gets no entity set
/// — it runs once per tick and does whatever global/spawn work the frame needs.</summary>
internal sealed class SpawnSystem : CallbackSystem
{
    private int _next;

    protected override void Configure(SystemBuilder b) => b
        .Name("Spawn")
        .Phase(Phase.Input)
        .Writes<Transform>().Writes<Bounds>().Writes<Ham>().Writes<Faction>().Writes<Wallet>().Writes<Intent>();

    protected override void Execute(TickContext ctx)
    {
        if (ctx.TickNumber == 0 || ctx.TickNumber % 30 != 0)
        {
            return;
        }
        int i = _next++;
        float x = 100f + (i * 37 % 800);
        float y = 100f + (i * 53 % 800);
        ctx.Transaction.Spawn<Character>(
            Character.Transform.Set(new Transform { Pos = new Point2F { X = x, Y = y }, Vel = new Point2F { X = 4f, Y = 2f } }),
            Character.Bounds.Set(new Bounds { Box = new AABB2F { MinX = x, MaxX = x, MinY = y, MaxY = y } }),
            Character.Ham.Set(new Ham { Health = 50, MaxHealth = 100, Action = 50, MaxAction = 100, Mind = 50, MaxMind = 100 }),
            Character.Faction.Set(new Faction { Value = i % 3 }),
            Character.Wallet.Set(new Wallet { Credits = 100 }),
            Character.Intent.Set(new Intent()));
        // no Commit — the scheduler commits this system's transaction at tick end.
    }
}

/// <summary>Move every character. A parallel QuerySystem: the engine fans this body across workers, each handling a slice
/// of <c>ctx.Entities</c>. Transform is SingleVersion, so the writes go through the per-worker <c>ctx.Accessor</c> —
/// no locks, no MVCC overhead.</summary>
internal sealed class MoveSystem : QuerySystem
{
    private const float World = 1000f;
    private readonly EcsView<Character> _characters;

    public MoveSystem(EcsView<Character> characters) => _characters = characters;

    protected override void Configure(SystemBuilder b) => b
        .Name("Move")
        .Phase(Phase.Simulation)
        .Input(() => _characters)
        .Parallel()
        .Writes<Transform>();

    protected override void Execute(TickContext ctx)
    {
        foreach (EntityId id in ctx.Entities)
        {
            var e = ctx.Accessor.OpenMut(id);
            ref var t = ref e.Write(Character.Transform);
            t.Pos = new Point2F
            {
                X = Wrap(t.Pos.X + t.Vel.X * ctx.DeltaTime),
                Y = Wrap(t.Pos.Y + t.Vel.Y * ctx.DeltaTime),
            };
        }
    }

    private static float Wrap(float v) => v < 0f ? v + World : (v > World ? v - World : v);
}

/// <summary>Keep the spatial index coherent after movement. Bounds carries the <c>[SpatialIndex]</c>, so it must be
/// written through the <c>WriteSpatial</c> barrier (a plain field write would trip the spatial analyzer). Cluster-native
/// loop — the high-throughput shape for touching a whole archetype SoA.</summary>
internal sealed class BoundsSyncSystem : QuerySystem
{
    private readonly EcsView<Character> _characters;

    public BoundsSyncSystem(EcsView<Character> characters) => _characters = characters;

    protected override void Configure(SystemBuilder b) => b
        .Name("BoundsSync")
        .Phase(Phase.Simulation)
        .Input(() => _characters)
        .Parallel()
        .After("Move")
        .ReadsFresh<Transform>()   // this tick's moved positions
        .Writes<Bounds>();

    protected override void Execute(TickContext ctx)
    {
        using var clusters = ctx.ClusterIds != null
            ? ctx.Accessor.GetClusterEnumerator<Character>(ctx.ClusterIds, ctx.StartClusterIndex, ctx.EndClusterIndex)
            : ctx.Accessor.GetClusterEnumerator<Character>(ctx.StartClusterIndex, ctx.EndClusterIndex);

        foreach (var cluster in clusters)
        {
            var bits = cluster.OccupancyBits;
            if (bits == 0)
            {
                continue;
            }

            var transforms = cluster.GetReadOnlySpan(Character.Transform);
            while (bits != 0)
            {
                int idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                var p = transforms[idx].Pos;
                cluster.WriteSpatial(Character.Bounds, idx, new Bounds { Box = new AABB2F { MinX = p.X, MaxX = p.X, MinY = p.Y, MaxY = p.Y } });
            }
        }
    }
}

/// <summary>Regenerate the HAM pools each tick — Health, Action and Mind all tick back toward their maxima, the way a
/// character recovers after a fight or a sprint. Ham is SingleVersion — a lock-free per-worker write, no MVCC revision.
/// Losing at most the last tick's regen on a crash is fine, which is exactly why it isn't Versioned.</summary>
internal sealed class RegenSystem : QuerySystem
{
    private readonly EcsView<Character> _characters;

    public RegenSystem(EcsView<Character> characters) => _characters = characters;

    protected override void Configure(SystemBuilder b) => b
        .Name("Regen")
        .Phase(Phase.Simulation)
        .Input(() => _characters)
        .Parallel()
        .Writes<Ham>();

    protected override void Execute(TickContext ctx)
    {
        foreach (EntityId id in ctx.Entities)
        {
            var e = ctx.Accessor.OpenMut(id);
            ref var h = ref e.Write(Character.Ham);
            h.Health = Math.Min(h.MaxHealth, h.Health + 1);
            h.Action = Math.Min(h.MaxAction, h.Action + 2);   // Action recovers fastest
            h.Mind = Math.Min(h.MaxMind, h.Mind + 1);
        }
    }
}

/// <summary>Give each character somewhere to go: refresh its Transient wander <see cref="Intent"/> and steer velocity
/// toward it. Intent is Transient (dropped on restart), so on a fresh run every character starts with a zero target and
/// picks a new one here — the sim comes back to life on reopen without any persisted AI state.</summary>
internal sealed class WanderSystem : QuerySystem
{
    private readonly EcsView<Character> _characters;

    public WanderSystem(EcsView<Character> characters) => _characters = characters;

    protected override void Configure(SystemBuilder b) => b
        .Name("Wander")
        .Phase(Phase.Simulation)
        .Input(() => _characters)
        .Parallel()
        .After("Move")
        .Writes<Transform>()   // steers velocity for next tick
        .Writes<Intent>();     // Transient wander target

    protected override void Execute(TickContext ctx)
    {
        foreach (EntityId id in ctx.Entities)
        {
            var e = ctx.Accessor.OpenMut(id);
            ref var intent = ref e.Write(Character.Intent);
            ref var t = ref e.Write(Character.Transform);

            // Transient Intent starts at (0,0) each run — seed a wander target derived from position.
            if (intent.Target.X == 0f && intent.Target.Y == 0f)
            {
                intent.Target = new Point2F { X = (t.Pos.X * 1.3f) % 1000f, Y = (t.Pos.Y * 0.7f + 250f) % 1000f };
            }

            float dx = intent.Target.X - t.Pos.X;
            float dy = intent.Target.Y - t.Pos.Y;
            float len = MathF.Sqrt(dx * dx + dy * dy);
            if (len > 2f)
            {
                t.Vel = new Point2F { X = dx / len * 5f, Y = dy / len * 5f };
            }
            else
            {
                intent.Target = default;   // reached → repick next tick
            }
        }
    }
}

/// <summary>Settle the economy. A handful of credit transfers per tick, each an atomic, snapshot-isolated move of
/// credits from one character's Versioned <see cref="Wallet"/> to another's. This is the ONLY Versioned write in the
/// tick loop and the ONLY place MVCC is paid for — a CallbackSystem runs it serially through <c>ctx.Transaction</c>
/// (the safe path for a cross-entity transfer), at event cadence, not per character per tick.</summary>
internal sealed class TradeSystem : CallbackSystem
{
    protected override void Configure(SystemBuilder b) => b
        .Name("Trade")
        .Phase(Phase.Simulation)
        .Writes<Wallet>();

    protected override void Execute(TickContext ctx)
    {
        // Event cadence, not every tick: settle trades every 10th tick. A Versioned write is ~6× a SingleVersion one,
        // so the economy is touched on a beat, while movement/HAM/AI run hot every tick.
        if (ctx.TickNumber % 10 != 0)
        {
            return;
        }

        var set = ctx.Transaction.Query<Character>().Execute();
        if (set.Count < 2)
        {
            return;
        }

        // Materialize the id set once so we can index pairs (Query().Execute() returns an unordered HashSet).
        var characters = new EntityId[set.Count];
        set.CopyTo(characters);
        int n = characters.Length;

        int pairs = Math.Min(4, n / 2);
        for (int k = 0; k < pairs; k++)
        {
            int ai = (int)((ctx.TickNumber * 7 + k * 2) % n);
            int bi = (ai + 1) % n;

            var from = ctx.Transaction.OpenMut(characters[ai]);
            ref var fromWallet = ref from.Write(Character.Wallet);
            long amount = Math.Min(10L, fromWallet.Credits);
            if (amount <= 0)
            {
                continue;
            }
            fromWallet.Credits -= amount;
            ctx.Transaction.OpenMut(characters[bi]).Write(Character.Wallet).Credits += amount;
        }
    }
}

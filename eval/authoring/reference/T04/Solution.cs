using System.Numerics;
using Eval;
using Typhon.Engine;

public static class Solution
{
    public static DatabaseEngine Open(string path) => DatabaseEngine.Open(path, o => o.Register<Health>().Register<Poison>().Register<Status>());

    public static EntityId SpawnCreature(DatabaseEngine db, int health, int poisonPerTick)
    {
        using var tx = db.CreateQuickTransaction();
        var id = tx.Spawn<Creature>(
            Creature.Health.Set(new Health { Value = health }),
            Creature.Poison.Set(new Poison { DamagePerTick = poisonPerTick }),
            Creature.Status.Set(new Status()));
        tx.Commit();
        return id;
    }

    public static TyphonRuntime CreateRuntime(DatabaseEngine db)
    {
        EcsView<Creature> view;
        using (var tx = db.CreateQuickTransaction())
        {
            view = tx.Query<Creature>().ToView();
        }
        return TyphonRuntime.Create(db, s => s.PublicTrack
            .DeclareDag("Sim")
            .Phases(Phase.Simulation)
            .Add(new PoisonSystem(view))
            .Add(new DeathSystem(view)));
    }

    public static bool IsDead(DatabaseEngine db, EntityId creature)
    {
        using var tx = db.CreateReadOnlyTransaction();
        return tx.Open(creature).Read(Creature.Status).Dead != 0;
    }
}

internal sealed class PoisonSystem : QuerySystem
{
    private readonly EcsView<Creature> _view;

    public PoisonSystem(EcsView<Creature> view) => _view = view;

    protected override void Configure(SystemBuilder b) => b
        .Name("Poison")
        .Phase(Phase.Simulation)
        .Input(() => _view)
        .Reads<Poison>()
        .Writes<Health>();

    protected override void Execute(TickContext ctx)
    {
        using var clusters = ctx.ClusterIds != null
            ? ctx.Accessor.GetClusterEnumerator<Creature>(ctx.ClusterIds, ctx.StartClusterIndex, ctx.EndClusterIndex)
            : ctx.Accessor.GetClusterEnumerator<Creature>(ctx.StartClusterIndex, ctx.EndClusterIndex);
        foreach (var cluster in clusters)
        {
            var bits = cluster.OccupancyBits;
            if (bits == 0)
            {
                continue;
            }
            var poison = cluster.GetReadOnlySpan(Creature.Poison);
            var health = cluster.GetSpan(Creature.Health);
            while (bits != 0)
            {
                int i = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                health[i].Value -= poison[i].DamagePerTick;
            }
            cluster.MarkDirty(Creature.Health);
        }
    }
}

internal sealed class DeathSystem : QuerySystem
{
    private readonly EcsView<Creature> _view;

    public DeathSystem(EcsView<Creature> view) => _view = view;

    protected override void Configure(SystemBuilder b) => b
        .Name("Death")
        .Phase(Phase.Simulation)
        .Input(() => _view)
        .After("Poison")
        .ReadsFresh<Health>()
        .Writes<Status>();

    protected override void Execute(TickContext ctx)
    {
        using var clusters = ctx.ClusterIds != null
            ? ctx.Accessor.GetClusterEnumerator<Creature>(ctx.ClusterIds, ctx.StartClusterIndex, ctx.EndClusterIndex)
            : ctx.Accessor.GetClusterEnumerator<Creature>(ctx.StartClusterIndex, ctx.EndClusterIndex);
        foreach (var cluster in clusters)
        {
            var bits = cluster.OccupancyBits;
            if (bits == 0)
            {
                continue;
            }
            var health = cluster.GetReadOnlySpan(Creature.Health);
            var status = cluster.GetSpan(Creature.Status);
            while (bits != 0)
            {
                int i = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                if (health[i].Value <= 0)
                {
                    status[i].Dead = 1;
                }
            }
            cluster.MarkDirty(Creature.Status);
        }
    }
}

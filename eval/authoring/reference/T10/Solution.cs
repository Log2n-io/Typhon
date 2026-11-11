using System.Numerics;
using Eval;
using Typhon.Engine;

public static class Solution
{
    public static DatabaseEngine Open(string path) => DatabaseEngine.Open(path, o => o
        .Register<UnitBounds>()
        .Register<UnitHealth>()
        .ConfigureSpatialGrid(SpatialGridConfig.Flat(Vector2.Zero, new Vector2(1000f, 1000f), cellSize: 50f)));

    public static TyphonRuntime CreateRuntime(DatabaseEngine db)
    {
        EcsView<Unit> view;
        using (var tx = db.CreateQuickTransaction())
        {
            view = tx.Query<Unit>().ToView();
        }
        return TyphonRuntime.Create(db, s => s.PublicTrack.DeclareDag("Sim").Phases(Phase.Simulation).Add(new RegenSystem(view)));
    }
}

internal sealed class RegenSystem : QuerySystem
{
    private readonly EcsView<Unit> _view;

    public RegenSystem(EcsView<Unit> view) => _view = view;

    protected override void Configure(SystemBuilder b) => b
        .Name("Regen")
        .Phase(Phase.Simulation)
        .Input(() => _view)
        .Parallel()
        .Writes<UnitHealth>();

    protected override void Execute(TickContext ctx)
    {
        using var clusters = ctx.ClusterIds != null
            ? ctx.Accessor.GetClusterEnumerator<Unit>(ctx.ClusterIds, ctx.StartClusterIndex, ctx.EndClusterIndex)
            : ctx.Accessor.GetClusterEnumerator<Unit>(ctx.StartClusterIndex, ctx.EndClusterIndex);
        foreach (var cluster in clusters)
        {
            var bits = cluster.OccupancyBits;
            if (bits == 0)
            {
                continue;
            }
            var hp = cluster.GetSpan(Unit.Health);
            bool any = false;
            while (bits != 0)
            {
                int i = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                if (hp[i].Value < hp[i].Max)
                {
                    hp[i].Value++;
                    any = true;
                    ctx.Subscriptions?.Replicate(in cluster, i);
                }
            }
            if (any)
            {
                cluster.MarkDirty(Unit.Health);
            }
        }
    }
}

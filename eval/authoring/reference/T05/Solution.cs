using System.Numerics;
using Eval;
using Typhon.Engine;

public static class Solution
{
    public static DatabaseEngine Open(string path) => DatabaseEngine.Open(path, o => o.Register<Position>().Register<Velocity>());

    public static TyphonRuntime CreateRuntime(DatabaseEngine db)
    {
        EcsView<Unit> view;
        using (var tx = db.CreateQuickTransaction())
        {
            view = tx.Query<Unit>().ToView();
        }
        return TyphonRuntime.Create(db, s => s.PublicTrack.DeclareDag("Sim").Phases(Phase.Simulation).Add(new MoveSystem(view)));
    }
}

internal sealed class MoveSystem : QuerySystem
{
    private readonly EcsView<Unit> _view;

    public MoveSystem(EcsView<Unit> view) => _view = view;

    protected override void Configure(SystemBuilder b) => b
        .Name("Move")
        .Phase(Phase.Simulation)
        .Input(() => _view)
        .Parallel()
        .Reads<Velocity>()
        .Writes<Position>();

    protected override void Execute(TickContext ctx)
    {
        using var clusters = ctx.ClusterIds != null
            ? ctx.Accessor.GetClusterEnumerator<Unit>(ctx.ClusterIds, ctx.StartClusterIndex, ctx.EndClusterIndex)
            : ctx.Accessor.GetClusterEnumerator<Unit>(ctx.StartClusterIndex, ctx.EndClusterIndex);
        float dt = ctx.DeltaTime;
        foreach (var cluster in clusters)
        {
            var bits = cluster.OccupancyBits;
            if (bits == 0)
            {
                continue;
            }
            var vel = cluster.GetReadOnlySpan(Unit.Velocity);
            var pos = cluster.GetSpan(Unit.Position);
            while (bits != 0)
            {
                int i = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                pos[i].X += vel[i].X * dt;
                pos[i].Y += vel[i].Y * dt;
            }
            cluster.MarkDirty(Unit.Position);
        }
    }
}

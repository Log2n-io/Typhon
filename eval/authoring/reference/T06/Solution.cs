using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Eval;
using Typhon.Engine;

public static class Solution
{
    private static readonly ConditionalWeakTable<TyphonRuntime, ConcurrentQueue<(EntityId, long)>> Queues = new();

    public static DatabaseEngine Open(string path) => DatabaseEngine.Open(path, o => o.Register<Purse>());

    public static TyphonRuntime CreateRuntime(DatabaseEngine db)
    {
        var queue = new ConcurrentQueue<(EntityId, long)>();
        var rt = TyphonRuntime.Create(db, s => s.PublicTrack.DeclareDag("Sim").Phases(Phase.Input).Add(new GrantSystem(queue)));
        Queues.Add(rt, queue);
        return rt;
    }

    public static void GrantGold(TyphonRuntime runtime, EntityId player, long amount)
        => Queues.GetValue(runtime, _ => throw new InvalidOperationException("unknown runtime")).Enqueue((player, amount));
}

// Request threads never touch the database: they enqueue, and this system applies the grants inside the tick.
internal sealed class GrantSystem : CallbackSystem
{
    private readonly ConcurrentQueue<(EntityId Player, long Amount)> _queue;

    public GrantSystem(ConcurrentQueue<(EntityId, long)> queue) => _queue = queue;

    protected override void Configure(SystemBuilder b) => b
        .Name("Grants")
        .Phase(Phase.Input)
        .Writes<Purse>();

    protected override void Execute(TickContext ctx)
    {
        while (_queue.TryDequeue(out var g))
        {
            if (!ctx.Transaction.TryOpenMut(g.Player, out var p))
            {
                continue;
            }
            var purse = p.Read(Player.Purse);
            purse.Gold += g.Amount;
            p.Set(Player.Purse, purse);
        }
    }
}

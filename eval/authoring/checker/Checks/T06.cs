using Eval;
using NUnit.Framework;
using Typhon.Engine;

namespace EvalChecker;

[TestFixture]
public class T06
{
    private static long Gold(DatabaseEngine db, EntityId id)
    {
        using var tx = db.CreateQuickTransaction();
        return tx.Open(id).Read(Player.Purse).Gold;
    }

    [Test]
    public void Grants_Routed_Through_Tick_Exactly_Once()
    {
        using var db = Solution.Open(H.NewDbPath("t06"));
        var players = new List<EntityId>();
        using (var tx = db.CreateQuickTransaction())
        {
            for (int i = 0; i < 4; i++)
            {
                players.Add(tx.Spawn<Player>(Player.Purse.Set(new Purse { Gold = 0 })));
            }
            tx.Commit();
        }

        using var rt = Solution.CreateRuntime(db);

        // Phase 1: grants before the runtime ticks. A solution that writes the SingleVersion purse from the calling thread
        // changes it right here: that write would race the tick fence once the runtime runs.
        Parallel.For(0, 400, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => Solution.GrantGold(rt, players[i % 4], 1));
        Assert.That(players.Sum(p => Gold(db, p)), Is.Zero,
            "GrantGold wrote the SingleVersion purse from the calling thread instead of routing it through a system");

        // Phase 2: grants while ticking.
        rt.Start();
        Parallel.For(0, 400, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => Solution.GrantGold(rt, players[i % 4], 1));
        bool done = H.WaitFor(() => players.Sum(p => Gold(db, p)) >= 800);
        long t = rt.CurrentTickNumber;
        H.WaitFor(() => rt.CurrentTickNumber >= t + 5);
        rt.Shutdown();
        Assert.That(done, Is.True, $"grants not applied: total {players.Sum(p => Gold(db, p))} of 800");
        foreach (var p in players)
        {
            Assert.That(Gold(db, p), Is.EqualTo(200), "a grant was lost or applied twice");
        }
    }
}

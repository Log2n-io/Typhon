using Eval;
using NUnit.Framework;
using Typhon.Engine;

namespace EvalChecker;

[TestFixture]
public class T04
{
    [Test]
    public void Deaths_Correct_After_Ticks()
    {
        using var db = Solution.Open(H.NewDbPath("t04"));
        var doomed = new List<EntityId>();
        var safe = new List<EntityId>();
        for (int i = 0; i < 200; i++)
        {
            doomed.Add(Solution.SpawnCreature(db, 5 + (i % 20), 1));     // dead by tick 25
            safe.Add(Solution.SpawnCreature(db, 100_000, 1));
            safe.Add(Solution.SpawnCreature(db, 10, 0));
        }
        using (var rt = Solution.CreateRuntime(db))
        {
            H.RunTicks(rt, 40);
        }
        Assert.That(doomed.Count(id => !Solution.IsDead(db, id)), Is.Zero, "poisoned creatures not marked dead");
        Assert.That(safe.Count(id => Solution.IsDead(db, id)), Is.Zero, "healthy creatures marked dead");
    }

    [Test]
    public void Declarations_Order_Death_After_Poison()
    {
        using var db = Solution.Open(H.NewDbPath("t04"));
        Solution.SpawnCreature(db, 10, 1);
        using var rt = Solution.CreateRuntime(db);
        var systems = rt.UserSystems;
        var poison = systems.Where(s => s.Access.Writes.Contains(typeof(Health))).ToList();
        var death = systems.Where(s => s.Access.Writes.Contains(typeof(Status))).ToList();
        Assert.That(poison, Is.Not.Empty, "no system declares Writes<Health>");
        Assert.That(death, Is.Not.Empty, "no system declares Writes<Status>");

        foreach (var p in poison)
        {
            Assert.That(H.ReadSet(p.Access), Does.Contain(typeof(Poison)), $"{p.Name} reads Poison without declaring it");
        }
        foreach (var d in death)
        {
            Assert.That(H.ReadSet(d.Access), Does.Contain(typeof(Health)), $"{d.Name} reads Health without declaring it");
            foreach (var p in poison)
            {
                Assert.That(H.OrderedAfter(systems, p, d), Is.True,
                    $"{d.Name} is not ordered after {p.Name}: it can see last tick's health (same-tick death not guaranteed)");
            }
        }
    }
}

using Eval;
using Typhon.Engine;

// Usage: Seed <database path>. Writes 100 heroes with Hp = i*10, Armor = i (i = 1..100) using the version-1 model.
using var db = DatabaseEngine.Open(args[0], o => o.Register<Stats>());
using (var tx = db.CreateQuickTransaction())
{
    for (int i = 1; i <= 100; i++)
    {
        tx.Spawn<Hero>(Hero.Stats.Set(new Stats { Hp = i * 10, Armor = i }));
    }
    tx.Commit();
}
return 0;

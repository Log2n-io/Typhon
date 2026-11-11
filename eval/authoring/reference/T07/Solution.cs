using Eval;
using Typhon.Engine;

public static class Solution
{
    public static DatabaseEngine Open(string path) => DatabaseEngine.Open(path, o => o
        .Register<Stats>()
        .Seed(1, tx =>
        {
            foreach (var id in tx.Query<Hero>().Execute())
            {
                var e = tx.OpenMut(id);
                var s = e.Read(Hero.Stats);
                s.Mana = 50;
                e.Set(Hero.Stats, s);
            }
        }));

    public static List<EntityId> AllHeroes(DatabaseEngine db)
    {
        using var tx = db.CreateReadOnlyTransaction();
        var list = new List<EntityId>();
        foreach (var id in tx.Query<Hero>().Execute())
        {
            list.Add(id);
        }
        return list;
    }

    public static (int Hp, int Armor, int Mana) ReadHero(DatabaseEngine db, EntityId hero)
    {
        using var tx = db.CreateReadOnlyTransaction();
        var s = tx.Open(hero).Read(Hero.Stats);
        return (s.Hp, s.Armor, s.Mana);
    }
}

using Typhon.Engine;
using Typhon.Schema.Definition;

[Component("Ref.Ranked", 1)]
public struct Ranked
{
    public String64 Name;
    [Index(AllowMultiple = true)]
    public int Level;
    private int _pad;
}

[Archetype]
public sealed partial class RankedPlayer : Archetype<RankedPlayer>
{
    public static readonly Comp<Ranked> Ranked = Register<Ranked>();
}

public static class Solution
{
    public static DatabaseEngine Open(string path) => DatabaseEngine.Open(path, o => o.Register<Ranked>());

    public static EntityId AddPlayer(DatabaseEngine db, string name, int level)
    {
        using var tx = db.CreateQuickTransaction();
        var id = tx.Spawn<RankedPlayer>(RankedPlayer.Ranked.Set(new Ranked { Name = name, Level = level }));
        tx.Commit();
        return id;
    }

    public static List<EntityId> FindAtLeastLevel(DatabaseEngine db, int minLevel)
    {
        using var tx = db.CreateReadOnlyTransaction();
        var result = new List<EntityId>();
        foreach (var id in tx.Query<RankedPlayer>().WhereField<Ranked>(r => r.Level >= minLevel).Execute())
        {
            result.Add(id);
        }
        return result;
    }
}

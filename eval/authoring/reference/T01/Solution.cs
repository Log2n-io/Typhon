using Typhon.Engine;
using Typhon.Schema.Definition;

[Component("Ref.Profile", 1)]
public struct Profile
{
    public String64 Name;
    public int Level;
    private int _pad;
}

[Archetype]
public sealed partial class PlayerProfile : Archetype<PlayerProfile>
{
    public static readonly Comp<Profile> Profile = Register<Profile>();
}

public static class Solution
{
    public static DatabaseEngine Open(string path) => DatabaseEngine.Open(path, o => o.Register<Profile>());

    public static EntityId CreatePlayer(DatabaseEngine db, string name, int level)
    {
        using var uow = db.CreateUnitOfWork(DurabilityMode.Immediate);
        using var tx = uow.CreateTransaction();
        var id = tx.Spawn<PlayerProfile>(PlayerProfile.Profile.Set(new Profile { Name = (String64)name, Level = level }));
        tx.Commit();
        return id;
    }

    public static void LevelUp(DatabaseEngine db, EntityId player)
    {
        using var uow = db.CreateUnitOfWork(DurabilityMode.Immediate);
        using var tx = uow.CreateTransaction();
        var e = tx.OpenMut(player);
        var p = e.Read(PlayerProfile.Profile);
        p.Level++;
        e.Set(PlayerProfile.Profile, p);
        // Rebase the +1 onto a concurrent commit instead of last-writer-wins.
        tx.Commit((ref ConcurrencyConflictSolver s) =>
        {
            var merged = s.CommittedData<Profile>();
            merged.Level += s.CommittingData<Profile>().Level - s.ReadData<Profile>().Level;
            s.ToCommitData<Profile>() = merged;
        });
    }

    public static (string Name, int Level) GetPlayer(DatabaseEngine db, EntityId player)
    {
        using var tx = db.CreateReadOnlyTransaction();
        var p = tx.Open(player).Read(PlayerProfile.Profile);
        return (p.Name.AsString, p.Level);
    }
}

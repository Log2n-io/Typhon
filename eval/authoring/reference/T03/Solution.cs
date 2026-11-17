using Typhon.Engine;
using Typhon.Schema.Definition;

[Component("Ref.Login", 1)]
public struct Login
{
    [Index]
    public String64 Value;
}

[Archetype]
public sealed partial class Account : Archetype<Account>
{
    public static readonly Comp<Login> Login = Register<Login>();
}

public static class Solution
{
    public static DatabaseEngine Open(string path) => DatabaseEngine.Open(path, o => o.Register<Login>());

    public static bool TryCreateAccount(DatabaseEngine db, string login)
    {
        try
        {
            using var tx = db.CreateQuickTransaction();
            tx.Spawn<Account>(Account.Login.Set(new Login { Value = login }));
            return tx.Commit();
        }
        catch (UniqueConstraintViolationException)
        {
            return false;
        }
    }

    public static int CountAccounts(DatabaseEngine db)
    {
        using var tx = db.CreateReadOnlyTransaction();
        return tx.Query<Account>().Count();
    }
}

using Eval;
using Typhon.Engine;

public static class Solution
{
    public static DatabaseEngine Open(string path) => DatabaseEngine.Open(path, o => o.Register<Balance>());

    public static EntityId CreateAccount(DatabaseEngine db)
    {
        using var tx = db.CreateQuickTransaction();
        var id = tx.Spawn<Account>(Account.Balance.Set(new Balance()));
        tx.Commit();
        return id;
    }

    // The audit is awaited before the transaction opens: a transaction is bound to its thread and must never span an await.
    public static async Task DepositAsync(DatabaseEngine db, EntityId account, long amount, Func<Task> auditAsync)
    {
        await auditAsync();
        Apply(db, account, amount);
    }

    private static void Apply(DatabaseEngine db, EntityId account, long amount)
    {
        using var tx = db.CreateQuickTransaction();
        var e = tx.OpenMut(account);
        var b = e.Read(Account.Balance);
        b.Amount += amount;
        e.Set(Account.Balance, b);
        // Concurrent deposits on one account: rebase this delta onto whatever committed meanwhile (default is last-writer-wins).
        tx.Commit((ref ConcurrencyConflictSolver s) =>
        {
            var merged = s.CommittedData<Balance>();
            merged.Amount += s.CommittingData<Balance>().Amount - s.ReadData<Balance>().Amount;
            s.ToCommitData<Balance>() = merged;
        });
    }

    public static long GetBalance(DatabaseEngine db, EntityId account)
    {
        using var tx = db.CreateReadOnlyTransaction();
        return tx.Open(account).Read(Account.Balance).Amount;
    }
}

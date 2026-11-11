## Deposits from an async API

`Model.cs` (given, do not modify) defines accounts with a Versioned `Balance`. An ASP.NET Core API calls `DepositAsync` from
request handlers, many at once; several deposits may target the same account at the same moment.

Each deposit must:
- await `auditAsync()` (it writes an audit record over the network), and
- add `amount` to the account's balance.

No deposit may be lost or applied twice, whatever the concurrency.

Contract:

```csharp
public static class Solution
{
    public static Typhon.Engine.DatabaseEngine Open(string path);

    public static Typhon.Engine.EntityId CreateAccount(Typhon.Engine.DatabaseEngine db);

    public static System.Threading.Tasks.Task DepositAsync(Typhon.Engine.DatabaseEngine db, Typhon.Engine.EntityId account, long amount,
        System.Func<System.Threading.Tasks.Task> auditAsync);

    public static long GetBalance(Typhon.Engine.DatabaseEngine db, Typhon.Engine.EntityId account);
}
```

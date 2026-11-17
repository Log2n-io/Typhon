## Unique account logins

Accounts are identified by a login (up to 32 ASCII characters). Two accounts may never share a login, even when many web
requests try to create the same login at the same moment on different threads. A refused creation is a normal outcome, not an
error: `TryCreateAccount` returns `false` and must not throw.

Contract:

```csharp
public static class Solution
{
    public static Typhon.Engine.DatabaseEngine Open(string path);

    // true when the account was created; false when the login is already taken. Thread-safe.
    public static bool TryCreateAccount(Typhon.Engine.DatabaseEngine db, string login);

    public static int CountAccounts(Typhon.Engine.DatabaseEngine db);
}
```

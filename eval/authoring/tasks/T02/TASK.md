## Level leaderboard

Players have a name (up to 40 ASCII characters) and a `Level` (int, 1–100). The game server holds hundreds of thousands of
players and calls `FindAtLeastLevel` many times per second, so it must be efficient: it must not visit every player.
Many players share the same level. Name the level field `Level`.

Contract:

```csharp
public static class Solution
{
    public static Typhon.Engine.DatabaseEngine Open(string path);

    public static Typhon.Engine.EntityId AddPlayer(Typhon.Engine.DatabaseEngine db, string name, int level);

    // Every player whose level is >= minLevel, in any order.
    public static System.Collections.Generic.List<Typhon.Engine.EntityId> FindAtLeastLevel(Typhon.Engine.DatabaseEngine db, int minLevel);
}
```

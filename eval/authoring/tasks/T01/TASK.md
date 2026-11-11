## Player profiles

Store player profiles: a display name (up to 40 ASCII characters) and a level (int). Profiles are durable data: they must
survive closing and reopening the database, and level changes must be atomic.

Contract:

```csharp
public static class Solution
{
    // Opens (or creates) the database at `path`, ready to use. The caller disposes the returned engine.
    public static Typhon.Engine.DatabaseEngine Open(string path);

    public static Typhon.Engine.EntityId CreatePlayer(Typhon.Engine.DatabaseEngine db, string name, int level);

    // Adds 1 to the player's level.
    public static void LevelUp(Typhon.Engine.DatabaseEngine db, Typhon.Engine.EntityId player);

    public static (string Name, int Level) GetPlayer(Typhon.Engine.DatabaseEngine db, Typhon.Engine.EntityId player);
}
```

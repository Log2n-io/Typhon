## Nearby entities

A 2D world spans 0..1000 on both axes. Entities are points. The server spawns entities and asks how many lie within a radius
of a point. Use the engine's spatial index (the production world holds millions of entities). `CountWithin` may be called
immediately after `Spawn` or `Move`, and must reflect every spawn and move made so far. This server has no `TyphonRuntime`
(no tick loop).

Contract:

```csharp
public static class Solution
{
    public static Typhon.Engine.DatabaseEngine Open(string path);

    public static Typhon.Engine.EntityId Spawn(Typhon.Engine.DatabaseEngine db, float x, float y);

    // Moves an existing entity to (x, y).
    public static void Move(Typhon.Engine.DatabaseEngine db, Typhon.Engine.EntityId entity, float x, float y);

    // Number of entities whose distance to (x, y) is <= radius.
    public static int CountWithin(Typhon.Engine.DatabaseEngine db, float x, float y, float radius);
}
```

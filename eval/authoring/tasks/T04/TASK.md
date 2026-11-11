## Poison and death

`Model.cs` (given, do not modify) defines creatures with `Health`, `Poison` and `Status`. The simulation runs in a
`TyphonRuntime` tick loop. Every tick:

1. each creature loses `Poison.DamagePerTick` health;
2. a creature whose health is at or below 0 is marked dead (`Status.Dead = 1`) **in the same tick** in which the poison
   brought it there (not one tick later).

Write the tick logic as Typhon systems. Several thousand creatures exist; the systems may run in parallel with other systems
added later, so they must be correct under the engine's scheduler.

Contract:

```csharp
public static class Solution
{
    public static Typhon.Engine.DatabaseEngine Open(string path);

    public static Typhon.Engine.EntityId SpawnCreature(Typhon.Engine.DatabaseEngine db, int health, int poisonPerTick);

    // Creates the runtime with your systems, NOT started. The caller starts, shuts down and disposes it.
    // All creatures are spawned before this is called.
    public static Typhon.Engine.TyphonRuntime CreateRuntime(Typhon.Engine.DatabaseEngine db);

    public static bool IsDead(Typhon.Engine.DatabaseEngine db, Typhon.Engine.EntityId creature);
}
```

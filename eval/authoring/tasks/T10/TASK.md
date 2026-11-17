## Health regeneration for a replicated world

`Model.cs` (given, do not modify) defines `Unit`, an archetype replicated to game clients through Typhon subscriptions.
Clients display each unit's health.

Write the system that regenerates health: every tick, each unit whose `UnitHealth.Value` is below `UnitHealth.Max` gains 1.
Clients must see the regenerated health. The world holds tens of thousands of units.

Contract:

```csharp
public static class Solution
{
    public static Typhon.Engine.DatabaseEngine Open(string path);

    // Creates the runtime with your regeneration system, NOT started. Subscriptions hosting (sessions, transport) is
    // configured elsewhere and is not part of this task.
    public static Typhon.Engine.TyphonRuntime CreateRuntime(Typhon.Engine.DatabaseEngine db);
}
```

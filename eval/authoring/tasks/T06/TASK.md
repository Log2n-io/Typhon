## Granting gold from web requests

`Model.cs` (given, do not modify) defines players with a `Purse` (SingleVersion). The game simulation runs in a
`TyphonRuntime` tick loop. The game's web API calls `GrantGold` at any time, from many request threads concurrently, typically
while the runtime is ticking. Every grant must be applied exactly once; none may be lost or applied twice. A grant does not have
to be visible immediately, but it must be applied within a few ticks.

Contract:

```csharp
public static class Solution
{
    public static Typhon.Engine.DatabaseEngine Open(string path);

    // Creates the runtime with your systems, NOT started. The caller starts, shuts down and disposes it.
    // Players are spawned (by the caller, through the Player archetype) before this is called.
    public static Typhon.Engine.TyphonRuntime CreateRuntime(Typhon.Engine.DatabaseEngine db);

    // Thread-safe; callable from any thread at any time.
    public static void GrantGold(Typhon.Engine.TyphonRuntime runtime, Typhon.Engine.EntityId player, long amount);
}
```

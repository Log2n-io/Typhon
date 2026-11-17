## Movement at scale

`Model.cs` (given, do not modify) defines units with a `Position` and a `Velocity`. Every tick, every unit moves:
`Position += Velocity * deltaTime`, with `deltaTime` the tick's delta time in seconds.

The production world holds about 100,000 units ticking at 60 Hz, so the movement must use the engine's fastest access path.
Positions are durable: after the runtime stops and the database is closed and reopened, every unit must be where it was when
the runtime stopped.

Contract:

```csharp
public static class Solution
{
    public static Typhon.Engine.DatabaseEngine Open(string path);

    // Creates the runtime with your systems, NOT started. The caller starts, shuts down and disposes it.
    // All units are spawned (by the caller, through the Unit archetype) before this is called.
    public static Typhon.Engine.TyphonRuntime CreateRuntime(Typhon.Engine.DatabaseEngine db);
}
```

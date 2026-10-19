# Asteroid Field — a Typhon server, mirrored in Unity

A belt of 5 000 drifting asteroids, a few mining drones, and one ship per pilot. The world lives in a Typhon engine; each Unity client sees the 300 m
around its ship, flies it, mines asteroids and sells the ore. Small on purpose, but every piece is the real thing: the server is ECS data simulated
by tick systems, and replication is *declared* — nothing in the server sends a byte by hand.

| Folder | What it is |
|---|---|
| `AsteroidField.Server/` | The server: schema (`Schema.cs`), the four tick systems and the replication declarations (`AsteroidServer.cs`), a console host |
| `Unity/` | The Unity 6.6 project (desktop player; no WebGL — the SDK uses sockets) |
| `Unity/Assets/AsteroidField/Core/` | The client core in plain C# 9: the connection, the per-frame snapshot, motion. Also compiled by the bot and the tests |
| `Unity/Assets/AsteroidField/Runtime/` | The MonoBehaviours: the game loop, entity views, camera, HUD, effects. The scene is built from code — any scene runs it |
| `AsteroidField.Bot/` | Headless pilots on the same client core, for company in the belt or a quick check that the server answers |
| `AsteroidField.Tests/` | End to end in one process: server and pilots over TCP (aux-tests gate) |

## What it shows of Typhon

- **Interest management** — a session holds what is within 300 m of its own ship (`Sphere(300).AroundControlled()`): about 70 asteroids of 5 000.
- **Motion segments** — an asteroid drifting straight is one segment until something changes it; the HUD's KiB/s is what keeping thousands in sync costs.
- **Commands** — steering (latest wins, rate-limited), mining (the target is a netId; the server's `TryResolve` refuses one this client was never shown),
  selling.
- **Owner-only state** — a ship's hold and credits reach its pilot alone.
- **Events** — a burst when an asteroid is mined out (best effort, to whoever is near) and "hold full" (reliable, to the pilot only).
- **Entity references** — a ship's mining target travels as the asteroid's netId, so every pilot in range draws every beam.
- **The SDK in Unity** — `Typhon.Client` and `Typhon.Protocol` built for `netstandard2.1`, with nothing else beside them.

## Run it

1. **Server** (from the repository root):
   ```bash
   dotnet run -c Release --project demo/AsteroidField/AsteroidField.Server
   ```
   It listens on `tcp://127.0.0.1:9300`. `--any` listens on every interface (a player on another machine), `--port`, `--asteroids`, `--drones`,
   `--hz` change the obvious.
2. **The SDK into the Unity project**, once and after any change to `src/Typhon.Protocol` or `src/Typhon.Client`:
   ```bash
   python3 demo/AsteroidField/build-unity-plugins.py
   ```
   It builds both for `netstandard2.1` into `Unity/Assets/Plugins/Typhon/` (git-ignored).
3. **Unity**: Unity Hub → *Add project from disk* → `demo/AsteroidField/Unity` (Unity 6.6, `6000.6.6f1`). Open any scene — the default empty one does —
   and press **Play**. The client builds its scene on load (`AsteroidFieldGame.Boot`) and connects to `127.0.0.1:9300` (the `Asteroid Field` object's
   inspector changes it).
4. **Fly**: **W/S** thrust, **A/D** turn, **left click** an asteroid to mine it (get within 60 m), **right click** to stop, **E** to sell your hold.
5. **Company**, optional: `dotnet run -c Release --project demo/AsteroidField/AsteroidField.Bot -- --bots 5` adds five headless pilots.

A **player build** (File → Build Profiles → Windows/Mac/Linux) takes `-host <name> -port <n>` on its command line.

## How the client is built

- **Threads.** The SDK applies frames on its own receive thread. `BeltLink` copies each frame — positions as motion segments, the fields the demo
  draws, the pilot's own state, the events — into a `BeltSnapshot` there; `Update` takes the newest. No Unity object is touched off the main thread,
  and nothing on the main thread reads the SDK's store.
- **Time.** `Typhon.Client.Clock` turns frame arrivals into a render time slightly behind the newest tick, so network jitter never shows; each entity
  is drawn at its segment extrapolated to that time, and eased toward it when a new segment corrects it.
- **Events** are received through the SDK's `EventCollector`, so no Unity script implements `IFieldSink` (whose `scoped` parameters C# 9 does not have).
- **No client-side prediction**: your own ship moves when the server says so, a render delay (≈ 150–300 ms) after your input. The honest baseline;
  prediction is a client's choice the SDK leaves to it.

## Checks

- `dotnet test demo/AsteroidField/AsteroidField.Tests` — the server and `BeltLink` pilots end to end: the view, the mining loop with its reliable event
  and the sale, another pilot seeing the beam, a target never shown refused.
- `python3 demo/AsteroidField/check-unity-scripts.py` — compiles the Unity scripts as Unity does (C# 9, `netstandard2.1`, against an installed editor's
  `UnityEngine` assemblies), without opening the editor. Local only: it needs Unity installed.

## Not proven here

The SDK's `netstandard2.1` build is tested on CoreCLR. This demo is its first run under Unity's Mono (the Editor and a Mono player); an IL2CPP player
build is the next step, and WebGL is out (no sockets).

If Unity reports *"multiple precompiled assemblies with the same name"* for `System.Text.Json` or `System.Runtime.CompilerServices.Unsafe`, a copy came in
from elsewhere (a package that bundles its own) — the plugins folder holds only the two Typhon DLLs, built against the versions Unity 6.6 ships.

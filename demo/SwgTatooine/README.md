# SWG Tatooine

A real-time 3D planet, Star Wars Galaxies' Tatooine, simulated by Typhon and played in a browser. It is the engine's
proving ground: a game server's workload run against the real engine.

- **The world**: terrain, seven towns of real buildings with interiors, NPCs, creatures, lairs, shuttles between
  cities; about 17 700 entities on a full planet, ticked by a Typhon system DAG and stored in a Typhon database.
- **The server** streams that world to every connected browser over a WebSocket: each session receives what is around
  it, and its commands (move, target, chat, board a shuttle) go back into the tick.
- **You can play**: sign up, get a character that persists, walk it with client-side prediction, collide with walls,
  ride a shuttle, close the tab and come back to the same body.
- **Load**: a bot fleet opens thousands of sessions against the same server, which is how the engine is measured.

## The projects

| Folder | What it is |
|---|---|
| `SwgTatooine.Server` | The simulation and the web host. Without `--serve` it is a measured benchmark run instead |
| `SwgTatooine.Client` | The browser client (TypeScript, Babylon.js, Vite) |
| `SwgTatooine.Assets` | The 3D assets and their pipeline; the bytes come from GitHub Releases ([README](SwgTatooine.Assets/README.md)) |
| `SwgTatooine.Bots` | The bot fleet: god cameras, player bots, a protocol fuzzer |
| `SwgTatooine.BotConsole` | A web console to set a fleet up, start it and watch the server |
| `SwgTatooine.Tests` | The checks (NUnit) |

## Running it locally

**Requirements:** .NET 10 SDK, Node.js 22, Python 3.11+. Every command below runs from the repository root, on Windows,
Linux or macOS.

1. **Fetch the assets.** The server will not serve without them: collision, doors, seats and terminals come from their
   descriptors.
   ```
   python demo/SwgTatooine/SwgTatooine.Assets/scripts/assets.py fetch published
   ```
   This unpacks the published set (~10 MB) into `SwgTatooine.Assets/build/assets/`, where the server finds it by itself.
   Fetching only `published` is enough to run the demo; editing assets needs the rest ([assets README](SwgTatooine.Assets/README.md)).

2. **Build the client.** `dotnet build` does not run Vite.
   ```
   npm --prefix demo/SwgTatooine/SwgTatooine.Client ci
   npm --prefix demo/SwgTatooine/SwgTatooine.Client run build
   ```

3. **Start the server.**
   ```
   dotnet run --project demo/SwgTatooine/SwgTatooine.Server -c Release -- --serve 5080 --pop 1.0
   ```
   Open <http://127.0.0.1:5080>. `--pop` scales the population (1.0 is the full planet); `--help` lists every flag.
   The server finds the client's `dist/` and the assets by walking up from its binary; `--client-dir` and `--assets`
   override them.

   On Windows, `scripts/swg-client.ps1` does steps 2 and 3 in one command, and `scripts/swg-serve.ps1` serves with the
   Workbench's live profiler attached.

**Working on the client:** keep the server running and start `npm run dev` in `SwgTatooine.Client`. Vite serves on
<http://localhost:5190> with hot reload and proxies `/ws`, `/auth`, `/typhon` and `/assets` to the server on port 5080.
`npm run check` runs typecheck, lint, format check and the unit tests.

## Bots

```
dotnet run --project demo/SwgTatooine/SwgTatooine.Bots -c Release -- --endpoint ws://127.0.0.1:5080/ws --kind god --bots 100
```

God sessions are anonymous cameras and need nothing else. Player bots (`--kind player`) are real accounts: they need
`--accounts-file <path>` and `SWG_BOT_KEY` in the environment, and the server's `/auth` limiter raised with
`--auth-burst`/`--auth-refill-ms`. `--help` on either program lists the rest.

On one machine, start the server with `--workers 16`: the engine's default uses every core and leaves the bots none.

## Tests

```
dotnet test demo/SwgTatooine/SwgTatooine.Tests -c Release --filter "TestCategory!=Quarantine"
SHARD_PROJECT=demo/SwgTatooine/SwgTatooine.Tests/SwgTatooine.Tests.csproj SHARD_PLAN=bench/aws/shards-swg.json python bench/aws/shard.py run
```

The second (bash) runs the suite the way the merge gate does, in four processes (~30 s instead of ~100 s); build the test
project in Release first. The checks build their own worlds and need neither the assets nor the client.

## Deploying

`scripts/publish-swg.ps1` builds one self-contained payload: the server, the built client and the asset set, with the
`run.sh` a systemd unit starts. `.github/workflows/tatooine-deploy.yml` builds it and puts it on an AWS stack.

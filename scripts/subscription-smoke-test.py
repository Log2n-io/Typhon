#!/usr/bin/env python3
"""End-to-end smoke test of the Subscriptions packages, from a local feed (design/Subscriptions/07-delivery.md § 5).

The anti-"packaging silently broke" gate for the client side, as consumer-smoke-test.sh is for the engine. From the packed .nupkg files (and,
optionally, the TypeScript SDK's `npm pack` tarball) it builds three fresh projects that reference nothing but those packages:

1. contracts — references only `Typhon.Protocol` and declares a `[ReplicatedMessage]` command: it implements `IReplicatedMessage` only if the
   replication generator shipped inside `Typhon.Protocol` and ran in a compilation with no engine.
2. server — references `Typhon`, `Typhon.Subscriptions.AspNetCore` and the contracts. Declares a `[Replicated]` archetype by attributes (the
   generator reaches it through `Typhon`'s dependency on `Typhon.Protocol`, once — a second copy would not compile), spawns three entities, and
   serves them over the engine's TCP listener and the ASP.NET Core WebSocket endpoint.
3. client — references only `Typhon.Client`: connects over TCP and must hold the three entities.

With --ts-tarball, a Node script installs the tarball and must hold the same three entities over WebSocket.

Usage: python3 scripts/subscription-smoke-test.py <feed-dir> [--ts-tarball <typhondb-client-x.y.z.tgz>]
Exit 0 = PASS.
"""

import argparse
import glob
import os
import queue
import re
import shutil
import subprocess
import sys
import tempfile
import threading

CONTRACTS_CS = r"""
using Typhon.Protocol;

namespace Smoke.Contracts;

[ReplicatedMessage]
public partial struct Hello
{
    [Codec(CodecKind.U16)] public ushort Value;
}
"""

SERVER_CS = r"""
using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Smoke.Contracts;
using Typhon.Engine;
using Typhon.Protocol;
using Typhon.Schema.Definition;
using Typhon.Subscriptions.AspNetCore;

if (!typeof(IReplicatedMessage).IsAssignableFrom(typeof(Hello)))
{
    throw new Exception("contracts: Hello does not implement IReplicatedMessage — the generator in Typhon.Protocol did not run");
}

using var dbe = DatabaseEngine.Open(System.IO.Path.Combine(args[0], "smoke.typhon"), o => o
    .Register<Body>()
    .ConfigureSpatialGrid(SpatialGridConfig.Flat(Vector2.Zero, new Vector2(1024f, 1024f), cellSize: 64f)));
using (var tx = dbe.CreateQuickTransaction())
{
    for (var i = 0; i < 3; i++)
    {
        var body = new Body { Bounds = new AABB2F { MinX = 10 + i * 20, MaxX = 11 + i * 20, MinY = 10, MaxY = 11 }, Level = (ushort)(i + 1) };
        tx.Spawn<Mob>(Mob.Body.Set(in body));
    }

    tx.Commit();
}

dbe.WriteTickFence(1);

using var runtime = TyphonRuntime.Create(dbe, schedule =>
{
    var dag = schedule.PublicTrack.DeclareDag("Smoke");
    dag.CallbackSystem("Sessions", ctx =>
    {
        var subs = ctx.Subscriptions;
        if (subs == null)
        {
            return;
        }

        foreach (ref readonly var e in subs.SessionEvents)
        {
            if (e.Kind == SessionEventKind.Opened)
            {
                subs.Session(e.Session).Profile("world");
            }
        }
    });
}, new RuntimeOptions
{
    WorkerCount = 1,
    BaseTickRate = 20,
    Subscriptions = new SubscriptionsOptions { ReplicationCellM = 64, IngressBytesPerSecond = 16 * 1024 },
});

var registry = runtime.Subscriptions;
registry.Sessions.Kinds("smoke");
registry.Archetype<Mob>();                      // as its attributes declare it: the generator must have run here
registry.Command<Hello>(c => { });             // its fields as its attributes declare them
registry.Profile("world", p => p.World().Of<Mob>());
runtime.Start();

var tcp = new TcpSubscriptionTransport(new TcpSubscriptionOptions { Port = 0 });
runtime.StartSubscriptionTransport(tcp);

var builder = WebApplication.CreateBuilder();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddSingleton(runtime);
builder.Services.AddTyphonSubscriptions(o => o.AllowAnyOrigin());
var app = builder.Build();
app.UseWebSockets();
app.MapTyphonSubscriptions("/ws");
app.StartAsync().GetAwaiter().GetResult();

Console.WriteLine($"TCP {tcp.BoundEndPoint.Port}");
Console.WriteLine($"WS {app.Urls.First().Replace("http://", "ws://")}/ws");
Console.Out.Flush();
while (Console.In.ReadLine() != null)
{
}

app.StopAsync().Wait(TimeSpan.FromSeconds(5));
tcp.StopAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
runtime.Shutdown();

[Component("Smoke.Body", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct Body
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;

    [Field]
    [Replicate(CodecKind.U16, Name = "level")]
    public ushort Level;

    [Field]
    public ushort Pad;
}

[Archetype]
[Replicated]
public sealed partial class Mob : Archetype<Mob>
{
    [Position]
    public static readonly Comp<Body> Body = Register<Body>();
}
"""

CLIENT_CS = r"""
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Client;

var held = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
await using var client = new TyphonClient(new ClientOptions { Endpoint = new Uri(args[0]), Kind = "smoke" });
client.FrameApplied += store =>
{
    var n = store.Archetypes.Sum(a => a.LiveCount);
    if (n == 3)
    {
        held.TrySetResult(n);
    }
};
await client.ConnectAsync();
var done = await Task.WhenAny(held.Task, Task.Delay(TimeSpan.FromSeconds(30)));
if (done != held.Task)
{
    throw new Exception($"client: timed out holding {client.Store?.Archetypes.Sum(a => a.LiveCount)} of 3 entities");
}

await client.CloseAsync();
Console.WriteLine("CLIENT OK: Typhon.Client holds the 3 entities over TCP");
"""

TS_CLIENT_MJS = r"""
import { Connection, FrameApplier } from '@typhondb/client';

let applier;
let held = false;
const timer = setTimeout(() => { console.error('ts: timed out'); process.exit(1); }, 30000);
const connection = new Connection({
  url: process.argv[2],
  kind: 'smoke',
  handlers: {
    onWelcome: (session) => { applier = new FrameApplier(session.plan); },
    onTick: (message, recvMs) => {
      applier.apply(message, recvMs);
      const n = applier.world.archetypes.reduce((sum, a) => sum + a.liveCount, 0);
      if (n === 3) {
        held = true;
        clearTimeout(timer);
        console.log('TS OK: @typhondb/client holds the 3 entities over WebSocket');
        connection.close();
        process.exit(0);
      }
    },
    onClose: (close) => { if (!held) { console.error('ts: closed', JSON.stringify(close)); process.exit(1); } },
  },
});
connection.connect();
"""


def run(cmd, cwd, **kw):
    print(f"smoke: $ {' '.join(cmd)}", flush=True)
    return subprocess.run(cmd, cwd=cwd, check=True, **kw)


def probe(cmd, cwd):
    """Runs a client; returns its stdout, and prints both streams when it fails, so a red run says why."""
    print(f"smoke: $ {' '.join(cmd)}", flush=True)
    r = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True, timeout=120)
    if r.returncode != 0:
        print(r.stdout, r.stderr, file=sys.stderr)
    return r.stdout


def project(path, sdk, refs, extra=""):
    items = "\n".join(refs)
    with open(path, "w", encoding="utf-8") as f:
        f.write(f"""<Project Sdk="{sdk}">
  <PropertyGroup>
    <OutputType>{"Library" if "contracts" in path else "Exe"}</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
{extra}  </PropertyGroup>
  <ItemGroup>
{items}
  </ItemGroup>
</Project>
""")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("feed")
    parser.add_argument("--ts-tarball")
    args = parser.parse_args()

    feed = os.path.abspath(args.feed)
    engine = [p for p in glob.glob(os.path.join(feed, "Typhon.[0-9]*.nupkg"))]
    if not engine:
        print(f"smoke: no Typhon.<version>.nupkg in {feed}", file=sys.stderr)
        return 1
    version = re.match(r"Typhon\.(.*)\.nupkg$", os.path.basename(engine[0])).group(1)
    for package in ("Typhon.Protocol", "Typhon.Client", "Typhon.Subscriptions.AspNetCore"):
        if not os.path.isfile(os.path.join(feed, f"{package}.{version}.nupkg")):
            print(f"smoke: {package}.{version}.nupkg missing: every package ships at one version", file=sys.stderr)
            return 1
    print(f"smoke: testing the Subscriptions packages {version} from {feed}", flush=True)

    # NuGet's global cache does not re-extract a same-version re-pack: evict ours, so a local re-run tests fresh content.
    cache = os.path.join(os.path.expanduser("~"), ".nuget", "packages")
    for package in ("typhon", "typhon.protocol", "typhon.client", "typhon.subscriptions.aspnetcore"):
        shutil.rmtree(os.path.join(cache, package, version), ignore_errors=True)

    work = tempfile.mkdtemp(prefix="typhon-subs-smoke-")
    server = None
    try:
        with open(os.path.join(work, "nuget.config"), "w", encoding="utf-8") as f:
            f.write(f"""<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="typhon-local" value="{feed}" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
""")
        pkg = lambda name: f'    <PackageReference Include="{name}" Version="{version}" />'
        for d in ("contracts", "server", "client"):
            os.makedirs(os.path.join(work, d))
        project(os.path.join(work, "contracts", "contracts.csproj"), "Microsoft.NET.Sdk", [pkg("Typhon.Protocol")])
        project(os.path.join(work, "server", "server.csproj"), "Microsoft.NET.Sdk.Web",
                [pkg("Typhon"), pkg("Typhon.Subscriptions.AspNetCore"), '    <ProjectReference Include="../contracts/contracts.csproj" />'])
        project(os.path.join(work, "client", "client.csproj"), "Microsoft.NET.Sdk", [pkg("Typhon.Client")])
        for d, source in (("contracts", CONTRACTS_CS), ("server", SERVER_CS), ("client", CLIENT_CS)):
            with open(os.path.join(work, d, "Program.cs"), "w", encoding="utf-8") as f:
                f.write(source)

        for d in ("server", "client"):
            run(["dotnet", "build", f"{d}/{d}.csproj", "-c", "Release", "-v", "quiet", "-nologo"], work)

        data = os.path.join(work, "data")
        os.makedirs(data)
        server = subprocess.Popen(["dotnet", "run", "--project", "server/server.csproj", "-c", "Release", "--no-build", "--", data], cwd=work,
                                  stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        lines = queue.Queue()
        threading.Thread(target=lambda: [lines.put(l) for l in server.stdout], daemon=True).start()
        tcp = ws = None
        while tcp is None or ws is None:
            try:
                line = lines.get(timeout=120)
            except queue.Empty:
                print("smoke: FAIL — the server never printed its endpoints", file=sys.stderr)
                return 1
            print(f"  server> {line.rstrip()}", flush=True)
            if line.startswith("TCP "):
                tcp = int(line.split()[1])
            elif line.startswith("WS "):
                ws = line.split()[1]
            elif server.poll() is not None:
                print("smoke: FAIL — the server exited", file=sys.stderr)
                return 1

        out = probe(["dotnet", "run", "--project", "client/client.csproj", "-c", "Release", "--no-build", "--", f"tcp://127.0.0.1:{tcp}"], work)
        print(f"  > {out.strip()}")
        if "CLIENT OK" not in out:
            print("smoke: FAIL — the .NET client did not hold the entities", file=sys.stderr)
            return 1

        if args.ts_tarball:
            ts = os.path.join(work, "ts")
            os.makedirs(ts)
            with open(os.path.join(ts, "package.json"), "w", encoding="utf-8") as f:
                f.write('{ "name": "smoke", "private": true, "type": "module" }\n')
            npm = "npm.cmd" if os.name == "nt" else "npm"
            run([npm, "install", "--no-audit", "--no-fund", os.path.abspath(args.ts_tarball)], ts)
            with open(os.path.join(ts, "client.mjs"), "w", encoding="utf-8") as f:
                f.write(TS_CLIENT_MJS)
            out = probe(["node", "client.mjs", ws], ts)
            print(f"  > {out.strip()}")
            if "TS OK" not in out:
                print("smoke: FAIL — the TypeScript client did not hold the entities", file=sys.stderr)
                return 1

        print("smoke: PASS")
        return 0
    finally:
        if server is not None:
            try:
                server.stdin.close()
                server.wait(timeout=20)
            except Exception:
                server.kill()
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())

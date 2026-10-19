---
uid: guide-packages
title: Packages
description: 'Every package Typhon ships — five on NuGet, one on npm, the native client SDK on GitHub Releases — what each is for, how to install it, and which ones a server or a client needs.'
---

# Packages

Typhon ships as **five NuGet packages**, **one npm package** and a **native SDK** on GitHub Releases. Every one of them is released
at **one version** — the engine and its clients always match — and the alpha line is **prerelease-only**, so installs need
`--prerelease` (NuGet) or the `@alpha` tag (npm).

> The version line runs alpha → beta (public API and wire protocol frozen) → rc → `1.0.0`. Expect breaking changes until beta.

## Server side

| Package | Gallery | Install | Targets | What it is |
|---|---|---|---|---|
| **Typhon** | [NuGet](https://www.nuget.org/packages/Typhon) | `dotnet add package Typhon --prerelease` | net10.0 | The engine, with its profiler, schema-definition APIs and the archetype-accessor source generator. Depends on `Typhon.Protocol`. |
| **Typhon.Cli** | [NuGet](https://www.nuget.org/packages/Typhon.Cli) | `dotnet tool install --global Typhon.Cli --prerelease` | net10.0 | The [`typhon` tool](../tools/cli/index.md): REPL, scripts, `typhon new`, `check` / `repair`, and the [Workbench](../tools/workbench/index.md) (`typhon ui`). |
| **Typhon.Subscriptions.AspNetCore** | [NuGet](https://www.nuget.org/packages/Typhon.Subscriptions.AspNetCore) | `dotnet add package Typhon.Subscriptions.AspNetCore --prerelease` | net10.0 | Lets **browsers** connect to your server. The engine's own listener speaks plain TCP, which a browser cannot open; this package adds a WebSocket endpoint for [Subscriptions](../feature-set/Subscriptions/transports-hosting.md) to an ASP.NET Core app you already run, so replication shares its port, TLS and middleware. It can also serve the server's catalog over HTTP, for tools and code generation. |

## Client side

| Package | Gallery | Install | Targets | What it is |
|---|---|---|---|---|
| **Typhon.Client** | [NuGet](https://www.nuget.org/packages/Typhon.Client) | `dotnet add package Typhon.Client --prerelease` | net10.0, netstandard2.1 (Unity) | The [.NET client SDK](../feature-set/Subscriptions/client-sdks.md) for game clients, bots, tools and tests, over TCP or WebSocket. |
| **Typhon.Protocol** | [NuGet](https://www.nuget.org/packages/Typhon.Protocol) | `dotnet add package Typhon.Protocol --prerelease` | net10.0, netstandard2.1 (Unity) | The `typhon.3` wire protocol and the replication attributes with their source generator. Reference it **alone** from a contracts assembly shared by a server and its .NET clients. |
| **@typhondb/client** | [npm](https://www.npmjs.com/package/@typhondb/client) | `npm install @typhondb/client@alpha` | ESM, Node ≥ 22 or a browser | The [TypeScript client SDK](../feature-set/Subscriptions/client-sdks.md). |
| **typhon-client-native** | [GitHub Releases](https://github.com/Log2n-io/Typhon/releases) | download `typhon-client-native-<version>-<platform>` | linux-x64, windows-x64, macos-arm64 | The [C client SDK](../feature-set/Subscriptions/client-sdks.md): headers, a static and a shared library, and a CMake package — `find_package(TyphonClient)`, then link `Typhon::typhon_client` or `Typhon::typhon_client_shared`. TCP only: no TLS or WebSocket yet. |

## Which ones do I need?

| You are building… | Install |
|---|---|
| An app or game server embedding the engine | `Typhon` (+ `Typhon.Cli` for the tooling) |
| …that serves browsers or WebSocket clients | + `Typhon.Subscriptions.AspNetCore` |
| A .NET or Unity game client, a bot, a test harness | `Typhon.Client` |
| Message contracts shared by server and .NET clients | `Typhon.Protocol` in the shared assembly |
| A browser or Node client | `@typhondb/client` |
| A native client, or bindings for another language | the native SDK |

The GitHub Release of each version also carries every `.nupkg` / `.snupkg` and the npm tarball, for offline or mirrored feeds.

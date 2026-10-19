---
uid: demo-asteroid-field
title: Asteroid Field
description: A Typhon server mirrored in Unity — a belt of 5 000 drifting asteroids, mining drones and player ships, replicated by declaration to Unity desktop clients over the Subscriptions SDK.
---

# Asteroid Field

**Asteroid Field** is a small client/server demo: a Typhon server simulates a belt of 5 000 drifting asteroids, a few mining drones and one ship per
pilot, and Unity 6.6 clients fly those ships. Where [AntHill](anthill.md) embeds the engine in the renderer's process, this one puts it behind the
network — the shape of a game server, with Typhon's Subscriptions doing the replication.

## What it proves

- **Replication by declaration.** The server declares what each pilot sees, sends and hears; no system writes a packet.
- **Interest management.** A pilot holds what is within 300 m of its ship — about 80 asteroids of 5 000 — and nothing beyond. Zooming out widens it
  to 1 200 m at run time (`SetRadius`), and distance bands send the far half every 4th tick.
- **Motion segments.** A rock drifting straight is one segment until it changes course: thousands of moving objects for a few KiB/s per client.
- **Commands with server-side checks.** Mining names its target by netId; the server refuses one the client was never shown.
- **Owner-only state and reliable events.** The hold and credits reach their pilot alone; "hold full" is reliable, a mined-out burst best effort.
- **The SDK in Unity.** `Typhon.Client` and `Typhon.Protocol` built for `netstandard2.1`, read on the main thread from a per-frame snapshot.

## Run it

The steps — server, the SDK into the Unity project, Play — are in the demo's
[README](https://github.com/Log2n-io/Typhon/blob/main/demo/AsteroidField/README.md).

| Dimension | Asteroid Field |
|---|---|
| **Entities** | 5 000 asteroids, 8 drones, one ship per pilot |
| **Topology** | Typhon server ↔ Unity clients over TCP |
| **Client** | Unity 6.6, desktop player (no WebGL) |
| **Per-client view** | 300 m sphere around the controlled ship, widened to 1 200 m by zooming out |
| **Typhon features** | Sphere interest, run-time radius, distance bands, motion segments, commands, owner fields, reliable events, entity references |

# @typhondb/client

**The TypeScript client SDK for [Typhon](https://typhondb.io) Subscriptions** — for browsers and Node.

Connects to a Typhon server over WebSocket, decodes its catalog-driven stream into a columnar world store, extrapolates motion between
frames, and sends commands back. Everything is decoded from the catalog the server sends at connection, so a client keeps working while
the server's schema evolves.

> ⚠️ **Alpha.** APIs and the wire format may still change between releases.

```bash
npm install @typhondb/client@alpha
```

```typescript
import { ReconnectingClient, FrameApplier, CommandQueue } from '@typhondb/client';

let applier: FrameApplier;
const client = new ReconnectingClient({
  url: 'wss://play.example.com/ws',
  kind: 'player',
  handlers: {
    onWelcome: (session) => {
      applier = new FrameApplier(session.plan);
    },
    onTick: (message, recvMs) => applier.apply(message, recvMs),
  },
});
```

- **Store**: per-archetype columns of what the server says this session holds (`applier.world`).
- **Motion**, **commands** (`CommandQueue`), **groups** (`GroupSubscriptions`, `applier.groups`), **events**, **aggregates**.
- `typhon-codegen` generates typed accessors from a server's catalog.

## Learn more

- [Client SDKs](https://doc.typhondb.io/latest/guides/feature-catalog/Subscriptions/client-sdks.html)
- [Subscriptions guide](https://doc.typhondb.io/latest/guides/07-subscriptions.html)

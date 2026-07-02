# Workbench Per-Session Schema Loading & ALC Reload
> Lets you rebuild and re-attach your schema DLL in the Workbench without restarting the host.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Schema](./README.md)

## 🎯 What it solves

Iterating on a schema while the Workbench is open normally hits a hard wall: .NET can't unload an assembly that's loaded into the default `AssemblyLoadContext`, so a rebuilt DLL can't replace the one already in memory without restarting the whole tool. It also doesn't help that a raw load failure, a breaking schema change, or a failed migration would otherwise just throw and tear down the session. This feature lets each Workbench session load your schema DLL in isolation, swap it for a freshly rebuilt one on the next attach, and turns engine-side schema exceptions into a banner state you can act on instead of a crash.

## ⚙️ How it works (in brief)

Each Open/Attach session loads your schema DLL(s) into a fresh, unloadable assembly context scoped to that session; closing the session releases it so the next attach can load the rebuilt binary cleanly. Every `[Component]`-tagged struct and every concrete type deriving from `Archetype<>`/`Archetype<,>` found in the DLL(s) is registered against the engine one at a time — a failure on one component doesn't stop the others from loading. The aggregate outcome becomes one of three session states: `Ready` (everything registered cleanly), `MigrationRequired` (at least one component registered, but something needs attention), or `Incompatible` (the session isn't navigable — a schema downgrade, a failed migration function, or total registration failure). Because each rebuild produces new CLR `Type` instances even for byte-identical source, the engine re-binds them to the same component IDs by matching the `[Component(Name, ...)]` string across reloads, so re-attaching never inflates component IDs or loses prior registrations.

## 💻 Usage

```csharp
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

// The Name string ("Game.Player") is the stable key the Workbench uses to re-bind this
// component across rebuilds. Keep it constant even as you change fields or bump Revision.
[Component("Game.Player", revision: 1)]
[StructLayout(LayoutKind.Sequential)]
public struct PlayerComponent
{
    public float Health;
    public int Level;
}
```

There's no API to call from application code — this is the Workbench's own loading behavior. As the application developer you just rebuild your schema project and re-attach (Open/Attach) the session; the Workbench handles the rest. What you control is the schema itself: keep `[Component]` names stable across edits, and be aware that a breaking field change or a removed migration function will surface as a `MigrationRequired` or `Incompatible` banner on the next attach rather than failing silently.

| Session outcome | When it happens | What you can still do |
|---|---|---|
| `Ready` | Every component registered with no diagnostics | Full session — browse, query, profile |
| `MigrationRequired` | At least one component registered; one or more had a breaking change, schema error, or other failure | Browse/inspect the components that loaded; fix and rebuild the rest |
| `Incompatible` | A schema downgrade, a failed migration function, or zero components registered | Session isn't usable until you fix the binary and re-attach |

## ⚠️ Guarantees & limits

- A bad or unbuildable DLL never crashes the Kestrel host or other open sessions — failures are scoped to the session being opened/attached.
- One component failing to register (breaking change, schema error) does not block its siblings from registering — you keep visibility into everything that's still readable.
- Re-attaching the same schema project after a rebuild reuses the same component IDs and archetype slot bindings — no ID growth across repeated edit/rebuild/re-attach cycles.
- `Incompatible` is reserved for unrecoverable cases (schema downgrade, failed migration, or total failure) — a handful of breaking-change components alongside healthy ones is `MigrationRequired`, not `Incompatible`.
- A schema DLL placed next to a stale copy of an engine assembly still resolves the engine's own copy (`Typhon.*`/`System.*`/`Microsoft.*` and anything already loaded are deferred to the host's context) — you won't silently get zero components from an attribute-identity mismatch.
- The session must fully close (and its components released) before the underlying DLL file is unlocked for a fresh build — keep the Workbench session closed while rebuilding if your build fails with a file-in-use error.
- Multi-file schemas (a main DLL plus sibling helper assemblies) resolve correctly only when the dependency sits in the same directory as the DLL you opened.

## 🧪 Tests

- [SchemaLoaderTests](../../../test/Typhon.Workbench.Tests/Schema/SchemaLoaderTests.cs) — missing path (`schema_missing`), invalid assembly (`schema_load_failed`), empty DLL list, real-assembly load through a fresh `WorkbenchAssemblyLoadContext`

## 🔗 Related

- Related feature: [Component Family Classification](component-family-classification.md) (a separate per-session classification step that runs after loading)
- Related feature: [Schema Validation on Reopen](schema-validation.md) (the underlying breaking/compatible classification this feature surfaces as a banner)
- Source: `tools/Typhon.Workbench/Schema/SchemaLoader.cs`, `tools/Typhon.Workbench/Schema/WorkbenchAssemblyLoadContext.cs`, `tools/Typhon.Workbench/Schema/SchemaCompatibility.cs`, `src/Typhon.Engine/Ecs/internals/ArchetypeRegistry.cs`

<!-- Deep dive: claude/design/Schema/06-workbench-schema-loading.md (§1-4: loader, compatibility classifier, ALC reload/rebind mechanics) -->
<!-- Related ADR: claude/adr/055-single-source-schema-resolution.md (where the DLL is resolved from — registered/bundled/legacy-adjacent search order) -->

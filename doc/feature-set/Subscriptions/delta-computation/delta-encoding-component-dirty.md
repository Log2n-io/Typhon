# Component-Level Dirty Encoding (v1)
> Today's wire encoding: a Modified entity sends the full bytes of every component that actually changed.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Subscriptions](../README.md)

## 🎯 What it solves

When an entity in a subscribed View changes, the client needs the new data — but sending every component on
that entity, every tick, wastes bandwidth on fields that didn't move. Component-level dirty encoding gives a
middle ground that needs no extra developer effort: skip components that weren't touched, but don't try to
track which individual fields inside a touched component changed (that's the v1.1 follow-up). It's the
encoding that ships today and the one every client/server pair on v1 should assume.

## ⚙️ How it works (in brief)

For each Modified entity, the encoder walks its enabled component slots and includes only the ones whose
storage chunk was marked dirty this tick (via `PreviousTickDirtyBitmap`); Versioned components have no dirty
bitmap and are always included when the entity is Modified. Each included component is wire-encoded as a
`ComponentFieldUpdate` with `FieldDirtyBits = ~0UL` (all bits set) and `FieldValues` holding the component's
complete raw bytes. The all-bits-set convention is what makes this forward-compatible: a client that only
understands "read the bitmask, then read the bytes it covers" works unmodified once the bitmask ever becomes
sparse — see [Per-Field Dirty Encoding (v1.1)](./delta-encoding-per-field-dirty.md).

## 💻 Usage

This is the only Modified-encoding path today — there is no opt-in/opt-out switch. It applies automatically
to every `Modified` entry your client receives:

```csharp
conn.OnTickDelta += tickDelta =>
{
    foreach (var viewDelta in tickDelta.Views)
    {
        foreach (var modified in viewDelta.Modified)
        {
            foreach (var update in modified.ChangedComponents)
            {
                // v1: FieldDirtyBits is always ~0UL — FieldValues is the full component struct.
                // Forward-compatible clients should still branch on FieldDirtyBits rather than
                // assuming "full struct" outright, since v1.1 will start sending sparse bitmasks.
                ApplyComponent(modified.Id, update.ComponentId, update.FieldValues);
            }
        }
    }
};
```

## ⚠️ Guarantees & limits

- **Whole-component granularity** — if any field in a component changed, the entire component's bytes are
  sent; there is no partial-component savings yet.
- **`FieldDirtyBits` is always `~0UL`** — clients must not hardcode "ignore the bitmask," since it will carry
  real per-field information once v1.1 ships; reading it now costs nothing and avoids a future client rewrite.
- **Unchanged components are omitted** — a Modified entity with five components but only one dirty chunk
  sends one `ComponentFieldUpdate`, not five.
- **Cost is proportional to dirty components, not entity count** — roughly 10-30µs to serialize ~200 Modified
  entities with ~2 dirty components each (~64 bytes per component), per the design doc's measured figures.

## 🧪 Tests

- [ProtocolTests](../../../../test/Typhon.Engine.Tests/Runtime/Subscriptions/ProtocolTests.cs) —
  `ComponentFieldUpdate_AllFieldsDirty_V1Format`: verifies the v1 wire convention (`FieldDirtyBits = ~0UL`, full
  component bytes)
- [ViewDeltaTests](../../../../test/Typhon.Engine.Tests/Runtime/Subscriptions/ViewDeltaTests.cs) —
  `DeltaBuilder_SharedView_ProducesCorrectDelta`: Added entities carry full component snapshot bytes

## 🔗 Related

- Code: `ComponentFieldUpdate` (`src/Typhon.Protocol/ComponentFieldUpdate.cs`), `EntityUpdate` (`src/Typhon.Protocol/EntityUpdate.cs`)
- Parent feature: [Per-Tick Delta Computation & Encoding](./README.md)
- Sibling: [Per-Field Dirty Encoding (v1.1)](./delta-encoding-per-field-dirty.md)

<!-- Deep dive: claude/design/Subscriptions/05-subscriptions.md — Modified Entity Encoding — Component-Level Dirty (v1) -->

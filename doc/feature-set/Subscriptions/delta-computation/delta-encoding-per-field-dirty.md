# Per-Field Dirty Encoding (v1.1)
> Planned: shrink Modified updates further by sending only the fields that changed inside a dirty component.

**Status:** 📋 Planned · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Subscriptions](../README.md)

## 🎯 What it solves

[Component-Level Dirty Encoding (v1)](./delta-encoding-component-dirty.md) already skips components that
didn't change, but a component with ten fields where only one moved still sends all ten. For wide components
that change one field at a time (a position component where only `Y` ticks, a stats block where only `Mana`
regenerates), that's bandwidth spent on bytes the client already has. Per-field dirty encoding is the planned
follow-up: shrink the payload to just the changed fields, with no wire-format migration needed because the
format was designed for this from the start.

## ⚙️ How it works (in brief)

The plan is an output-phase diff: the runtime would keep the previous tick's component snapshot per published
View and, for each Modified entity's dirty component, memcmp it field-by-field against that snapshot. Fields
that differ set their bit in `FieldDirtyBits`; `FieldValues` would carry only the bytes for those fields,
concatenated in field-index order, instead of the full struct. The cost lands entirely in the Output phase —
no write-path overhead — and the design doc estimates roughly 60% bandwidth savings on Modified entities. The
wire format already supports this without a protocol bump: `ComponentFieldUpdate.FieldDirtyBits` exists today
and v1 already sets it (to `~0UL`); v1.1 only changes which bits get set and how much of `FieldValues` gets
written.

## 💻 Usage

Not implemented — there is no separate API to enable this; it would replace how `FieldValues` is populated
for existing Modified deltas. The current reference client (`src/Typhon.Client/ViewSubscription.cs`) is
written to handle it once it lands:

```csharp
// Illustrative only — design complete, not implemented yet.
// A forward-compatible client already keys off FieldDirtyBits, not "always full struct":
foreach (var update in modified.ChangedComponents)
{
    if (update.FieldDirtyBits == ~0UL)
    {
        ApplyFullComponent(modified.Id, update.ComponentId, update.FieldValues);   // v1 path
    }
    else
    {
        ApplySparseFields(modified.Id, update.ComponentId, update.FieldDirtyBits, update.FieldValues); // v1.1 path
    }
}
```

## ⚠️ Guarantees & limits

- **Not implemented.** No code under `src/Typhon.Engine/Subscriptions` performs per-field diffing today;
  `DeltaBuilder` always sets `FieldDirtyBits = ~0UL` and sends full component bytes (v1).
- **No wire-format change required when it ships** — `ComponentFieldUpdate` already carries the bitmask field;
  v1.1 is expected to be a server-side encoding change plus a client decode change, not a new message type.
- **Expected scope: Output-phase diffing only** — the plan keeps one previous-tick snapshot per published
  View; it does not propose any write-path instrumentation or per-field change tracking during simulation.
- A client written against v1 that ignores `FieldDirtyBits` and always treats `FieldValues` as the full struct
  will silently mis-decode once v1.1 ships — read the bitmask now to avoid a future client-side bug.

## 🔗 Related

- Code: `ComponentFieldUpdate` (`src/Typhon.Protocol/ComponentFieldUpdate.cs`) — the bitmask field this sub-feature would populate
- Parent feature: [Per-Tick Delta Computation & Encoding](./README.md)
- Sibling: [Component-Level Dirty Encoding (v1)](./delta-encoding-component-dirty.md)

<!-- Deep dive: claude/design/Subscriptions/05-subscriptions.md — Modified Entity Encoding (v1.1 planned), R-Q18, Scope (v1 / Demo) -->

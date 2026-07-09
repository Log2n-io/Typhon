---
uid: feature-schema-migration-progress-tracking
title: 'Migration Progress Tracking'
description: 'A phase-by-phase event stream for observing long-running schema migrations as they happen.'
---

# Migration Progress Tracking
> A phase-by-phase event stream for observing long-running schema migrations as they happen.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Schema](./README.md)

## 🎯 What it solves

When a component's persisted layout differs from the running application's struct definition, Typhon migrates existing entities to the new layout automatically when the component is registered — synchronously, on the calling thread. For a large table this can take measurable time, and a caller with no visibility into it just sees registration "hang." `OnMigrationProgress` gives operators and tooling a live feed of what the migration is doing, how far it has gotten, and how long it has taken, so this can be surfaced in logs, a CLI progress bar, or a deployment dashboard instead of being a black box.

## ⚙️ How it works (in brief)

`DatabaseEngine` exposes a standard .NET event, `OnMigrationProgress`, of type `EventHandler<MigrationProgressEventArgs>`. Subscribe to it before calling `RegisterComponent`/`RegisterComponentFromAccessor`/`RegisterComponentByType`; if that registration triggers a migration (compatible stride change, or a breaking change with a registered migration function), the engine raises one event per phase as it executes, on the same thread, before the registration call returns. Each event reports the component name, the current `MigrationPhase`, entity counts, percent complete, and elapsed time. If registration doesn't trigger a migration, no events fire.

## 💻 Usage

```csharp
var phases = new List<MigrationPhase>();

dbe.OnMigrationProgress += (sender, args) =>
{
    Console.WriteLine($"[{args.ComponentName}] {args.Phase} " +
                       $"{args.EntitiesMigrated}/{args.TotalEntities} " +
                       $"({args.PercentComplete:F1}%) elapsed={args.Elapsed.TotalMilliseconds:F1}ms");
    phases.Add(args.Phase);
};

// Migration (if needed) runs synchronously inside this call.
dbe.RegisterComponentFromAccessor<PlayerV2>();
```

`MigrationPhase` values, in emission order: `Analyzing`, `AllocatingSegments`, `MigratingEntities`, `RecreatingRevisionChain`, `Flushing`, `Complete`. (`BuildingNewIndexes` and `UpdatingMetadata` are reserved phase values not currently emitted by the engine.)

## ⚠️ Guarantees & limits

- Events are raised synchronously on the thread that called the `RegisterComponent*` method — there is no background thread or async dispatch.
- Phases are emitted in monotonically non-decreasing order; the first event is always `Analyzing` and the last is always `Complete`.
- `EstimatedRemaining` and `PercentComplete` are coarse, phase-based estimates, not a fine-grained per-entity progress meter — don't rely on them for precise ETAs.
- Only fires for migrations triggered during component registration at database open. `DatabaseSchema.ValidateEvolution` (dry-run) does not raise progress events since it never migrates anything.
- No subscribers means no overhead beyond the null-check on the event invocation.

## 🧪 Tests

- [OperationalToolingTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/Schema/OperationalToolingTests.cs) — `MigrationProgress_EventsFiredInOrder` asserts the phase list starts at `Analyzing`, ends at `Complete`, and is monotonically non-decreasing

## 🔗 Related

- Related feature: [Offline Schema Inspection & Dry-Run Validation](./schema-inspection-dryrun.md) (predicts whether a migration will run, without running it)
- Related feature: [Schema Validation on Reopen](./schema-validation.md) (the broader auto-migration flow this progress stream observes)

<!-- Design: claude/design/Schema/05-operational-tooling.md §4 Migration Progress Tracking -->

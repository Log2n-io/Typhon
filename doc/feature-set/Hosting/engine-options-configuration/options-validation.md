---
uid: feature-hosting-engine-options-configuration-options-validation
title: 'Options Validation'
description: 'Engine and storage options are range-checked at DI resolution, so a bad configuration throws at startup instead of at first use.'
---

# Options Validation
> Engine and storage options are range-checked at DI resolution, so a bad configuration throws at startup instead of at first use.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Hosting](../README.md)

## 🎯 What it solves

A misconfigured option — a zero WAL segment size, a negative checkpoint interval, a database name
the filesystem will reject — is cheap to catch at startup and expensive to discover later, when the
engine is mid-open and the stack trace points at a subsystem rather than at your `Add*()` call.
.NET's Options pattern already has the hook for this: an `IValidateOptions<T>` runs on first
`IOptions<T>.Value` access and throws `OptionsValidationException` before the bad value reaches a
service constructor. Typhon wires two real validators into it.

## ⚙️ How it works (in brief)

Two `IValidateOptions<T>` implementations live in
[`Hosting/internals/OptionsValidators.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Hosting/internals/OptionsValidators.cs)
and are registered by the `Add*()` extension that owns each options type:

| Validator | Registered by | Checks |
|---|---|---|
| `DatabaseEngineOptionsValidator` | `AddDatabaseEngine` | `Resources` is non-null; `MaxActiveTransactions`, `WalRingBufferSizeBytes`, `CheckpointIntervalMs`, `CheckpointBarrierTimeoutMs` are all > 0; and when `Wal` is present, its `SegmentSize`, `PreAllocateSegments`, `StagingBufferSize` and `GroupCommitIntervalMs` |
| `PagedMMFOptionsValidator<TO>` | `AddPagedMMF` / `AddManagedPagedMMF` | delegates to `PagedMMFOptions.Validate(silent, out message)` — `DatabaseName`, `DatabaseDirectory`, `DatabaseCacheSize` well-formedness — and surfaces its specific rule message |

Failures accumulate rather than short-circuiting, so one startup reports every bad knob instead of
making you fix them one restart at a time. A `null` `Wal` is *not* an error — the engine derives WAL
defaults when none is supplied, so the validator only checks a `WalWriterOptions` you actually set.

## 💻 Usage

Validation is automatic. There is nothing to call:

```csharp
services
    .AddManagedPagedMMF(o =>
    {
        o.DatabaseName      = "MyGame";
        o.DatabaseCacheSize = 4096;          // too small — rejected at DI resolution
    })
    .AddDatabaseEngine(o =>
    {
        o.Resources.CheckpointIntervalMs = 0; // non-positive — rejected too
    });

// Throws OptionsValidationException naming the offending rule, before the engine opens a file.
using var provider = services.BuildServiceProvider();
var engine = provider.GetRequiredService<DatabaseEngine>();
```

`PagedMMFOptions.IsValid` / `Validate(bool silent, out string)` remain public, so you can also
check a configuration you built by hand before handing it to DI. That is the same method the
validator calls — one source of truth for storage-config rules, two entry points.

## ⚠️ Guarantees & limits

- **A validator is only registered when you pass a `configure` delegate** to the `Add*()` call.
  `AddDatabaseEngine()` with no delegate registers nothing to validate, because there is nothing
  the caller configured — defaults are valid by construction.
- **Range-checking is not budgeting.** Nothing sums your allocations against a memory ceiling: a
  configuration where every individual knob is in range can still ask for more RAM than the machine
  has. The `ResourceOptions.TotalMemoryBudgetBytes` / `Validate()` pair that once implied otherwise
  was removed in #148 — it governed no allocation.
- Validation runs at **first `IOptions<T>.Value` access**, which for these types is the first
  resolution of the service that consumes them — not at `BuildServiceProvider()`.
- `MemoryAllocatorOptions` and `ResourceRegistryOptions` have **no** validator today; their
  `Add*()` extensions configure without registering one.

## 🧪 Tests

- [ResourceOptionsTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Resources/ResourceOptionsTests.cs) — asserts the shipped `ResourceOptions` defaults are sensible, plus the `ExhaustionPolicy` / `ResourceExhaustedException` surface

## 🔗 Related

- Source: [`OptionsValidators.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Hosting/internals/OptionsValidators.cs) (both validators), [`TyphonBuilderExtensions.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Hosting/public/TyphonBuilderExtensions.cs) (the two registration sites), [`PagedMMFOptions.cs` — `IsValid`/`Validate`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Storage/public/PagedMMFOptions.cs)
- Parent feature: [Engine Options Configuration Surface](./README.md)
- Sibling: [DI Engine Bootstrap Chain](../di-bootstrap-chain/README.md) — the `Add*()` calls these validators hang off
- Sibling: [Resource Budgets & Options](../../Resources/resource-budgets-options.md) — what the `Resources` knobs actually control

<!-- Deep dive: claude/design/Hosting/di-extensions.md -->

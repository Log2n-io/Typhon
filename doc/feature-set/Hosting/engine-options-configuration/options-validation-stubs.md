---
uid: feature-hosting-engine-options-configuration-options-validation-stubs
title: 'Options Validation Hooks'
description: 'The AddOptions().Validate(...) wiring exists on every options type today, but its predicate is a no-op stub.'
---

# Options Validation Hooks
> The `AddOptions<T>().Validate(...)` wiring exists on every options type today, but its predicate is a no-op stub.

**Status:** 🚧 Partial · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Hosting](../README.md)

## 🎯 What it solves

.NET's Options pattern supports fail-fast validation: `AddOptions<T>().Validate(predicate)` runs
the predicate on first `IOptions<T>.Value` access and throws `OptionsValidationException` before
bad configuration reaches a service constructor. Typhon attaches this hook to every options type
(`DatabaseEngineOptions`, `PagedMMFOptions`/`ManagedPagedMMFOptions`, `MemoryAllocatorOptions`,
`ResourceRegistryOptions`) so real validation logic has one designated place to land per
subsystem, without changing any `Add*()` call signature when it does.

## ⚙️ How it works (in brief)

Every `Add*` extension that accepts a `configure` delegate attaches
`optionsBuilder.Validate(_ => { /* TODO */ return true; })` right after `.Configure(configure)`.
All four sites currently return `true` unconditionally — the hook fires but never rejects
anything. `PagedMMFOptions` separately exposes its own standalone validation you can call
yourself: `PagedMMFOptions.IsValid` / `Validate(bool silent, out string)` (checks `DatabaseName`,
`DatabaseDirectory`, and `DatabaseCacheSize` well-formedness).

> ⚠️ **This page describes a mechanism #148 replaced.** `ResourceOptions.Validate()` and
> `TotalMemoryBudgetBytes` no longer exist, and the `Add*()` sites no longer carry
> `Validate(_ => true)` stubs: `DatabaseEngineOptionsValidator` is registered as a real
> `IValidateOptions<DatabaseEngineOptions>` and range-checks the wired `Resources` knobs plus
> `WalWriterOptions` at DI resolution. The page needs a rewrite; tracked separately from this pass.

## 💻 Usage

```csharp
var resourceOptions = new ResourceOptions
{
    PageCachePages         = 262144,      // 2 GB
    MaxActiveTransactions  = 1000,
    WalRingBufferSizeBytes = 8 << 20,     // 8 MB — an explicit OVERRIDE; the default is 64 MB
};
resourceOptions.Validate();               // throws InvalidOperationException if over budget — call it yourself

var mmfOptions = new ManagedPagedMMFOptions { DatabaseName = "MyGame", DatabaseCacheSize = 4096 };
if (!mmfOptions.IsValid)
{
    mmfOptions.Validate(silent: false, out _);   // throws with a readable message
}

services
    .AddManagedPagedMMF(o =>
    {
        o.DatabaseName      = mmfOptions.DatabaseName;
        o.DatabaseCacheSize = mmfOptions.DatabaseCacheSize;
    })
    .AddDatabaseEngine(o => o.Resources = resourceOptions);
    // AddOptions<T>().Validate(...) still runs for both calls above, but its predicate
    // always returns true — it will not catch a bad DatabaseCacheSize or budget overrun.
```

## ⚠️ Guarantees & limits

- The `.Validate(...)` hook attached inside `Add*()` is wired but **non-functional** — its
  predicate is `_ => true` (marked `// TODO` in source, four separate sites). It never throws
  `OptionsValidationException`, regardless of how invalid the configured values are.
- The hook is only attached when a `configure` delegate is passed to the `Add*()` call — calling
  it with no delegate skips even the stub.
- `PagedMMFOptions.IsValid` / `Validate(bool, out string)` is real, independent, and callable
  today — but nothing in the `Add*()`/DI path calls it for you.
- `ResourceOptions` no longer has a `Validate()` of its own (#148). Its wired knobs, and
  `WalWriterOptions`, are range-checked by `DatabaseEngineOptionsValidator` at DI resolution.
- Do not rely on `BuildServiceProvider()` or `GetRequiredService<DatabaseEngine>()` to surface
  *every* configuration mistake — an invalid database name or an oversized cache is still not
  caught before the engine attempts to open its backing files.

## 🧪 Tests

- [ResourceOptionsTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Resources/ResourceOptionsTests.cs) — asserts the shipped `ResourceOptions` defaults are sensible, plus the `ExhaustionPolicy` / `ResourceExhaustedException` surface. It no longer covers a `ResourceOptions.Validate()`, that method having been removed in #148

## 🔗 Related

- Source: [`TyphonBuilderExtensions.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Hosting/public/TyphonBuilderExtensions.cs) (`ConfigureMemoryAllocatorOptions`, `ConfigureResourceRegistryOptions`, `AddPagedMMF`, `AddDatabaseEngine` — the four `.Validate(_ => true)` sites), [`ResourceOptions.cs` — `Validate()`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Resources/public/ResourceOptions.cs), [`PagedMMFOptions.cs` — `IsValid`/`Validate`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Storage/public/PagedMMFOptions.cs)
- Parent feature: [Engine Options Configuration Surface](./README.md)
- Sibling: [DI Engine Bootstrap Chain](../di-bootstrap-chain/README.md) — the `Add*()` calls whose `configure` delegate this stubbed hook should fail-fast on.

<!-- Deep dive: claude/design/Hosting/di-extensions.md — "Validation hooks are stubs" -->

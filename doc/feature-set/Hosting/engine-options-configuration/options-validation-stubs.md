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
anything. Two option types separately expose their own real, standalone validation you can call
yourself: `ResourceOptions.Validate()` (throws `InvalidOperationException` if page cache + WAL +
shadow-buffer sizing exceeds `TotalMemoryBudgetBytes`) and `PagedMMFOptions.IsValid` /
`Validate(bool silent, out string)` (checks `DatabaseName`, `DatabaseDirectory`, and
`DatabaseCacheSize` well-formedness). Neither is wired into the `Add*()`/DI path — invoking them
is on you.

## 💻 Usage

```csharp
var resourceOptions = new ResourceOptions
{
    PageCachePages         = 262144,      // 2 GB
    MaxActiveTransactions  = 1000,
    WalRingBufferSizeBytes = 8 << 20,     // 8 MB
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
- `ResourceOptions.Validate()` and `PagedMMFOptions.IsValid` / `Validate(bool, out string)` are
  real, independent, and callable today — but nothing in the `Add*()`/DI path calls them for you.
- Do not rely on `BuildServiceProvider()` or `GetRequiredService<DatabaseEngine>()` to surface a
  configuration mistake (oversized cache, invalid database name, WAL sizing over budget) — none
  of these are caught before the engine attempts to open its backing files.
- Until the stubs are implemented, calling `ResourceOptions.Validate()` / `PagedMMFOptions.IsValid`
  yourself — before or inside your `configure` delegate — is the only fail-fast path available.

## 🧪 Tests

- [ResourceOptionsTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Resources/ResourceOptionsTests.cs) — the real, callable `ResourceOptions.Validate()`: passes on defaults/large budgets, throws `InvalidOperationException` with a readable message when fixed allocations exceed `TotalMemoryBudgetBytes`; no test exercises the `Add*()`-wired `.Validate(_ => true)` stub itself (there is nothing to assert — it never rejects)

## 🔗 Related

- Source: [`TyphonBuilderExtensions.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Hosting/public/TyphonBuilderExtensions.cs) (`ConfigureMemoryAllocatorOptions`, `ConfigureResourceRegistryOptions`, `AddPagedMMF`, `AddDatabaseEngine` — the four `.Validate(_ => true)` sites), [`ResourceOptions.cs` — `Validate()`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Resources/public/ResourceOptions.cs), [`PagedMMFOptions.cs` — `IsValid`/`Validate`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Storage/public/PagedMMFOptions.cs)
- Parent feature: [Engine Options Configuration Surface](./README.md)
- Sibling: [DI Engine Bootstrap Chain](../di-bootstrap-chain/README.md) — the `Add*()` calls whose `configure` delegate this stubbed hook should fail-fast on.

<!-- Deep dive: claude/design/Hosting/di-extensions.md — "Validation hooks are stubs" -->

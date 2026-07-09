---
uid: feature-errors-result-type
title: 'Result Hot-Path Result Type'
description: 'Zero-allocation dual-generic result struct for hot-path lookups that expect a miss — no exceptions, no boxing, no branch on access.'
---

# Result<TValue,TStatus> Hot-Path Result Type
> Zero-allocation dual-generic result struct for hot-path lookups that expect a miss — no exceptions, no boxing, no branch on access.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Errors](./README.md)

## 🎯 What it solves

Some lookups fail routinely as part of normal operation — a key isn't in the index yet, a component revision isn't visible at your snapshot tick, an entity was deleted. Throwing an exception for these expected outcomes is disproportionately expensive on paths that run millions of times per second (B+Tree lookups, MVCC revision-chain reads). A plain `bool` return loses information — every kind of "no" collapses into the same `false`, so callers can't tell "never existed" from "tombstoned" from "not visible yet." `Result<TValue, TStatus>` gives these hot paths a typed, zero-allocation way to say "here's your value" or "here's exactly why not," without paying exception overhead for outcomes that aren't errors.

## ⚙️ How it works (in brief)

`Result<TValue, TStatus>` is a readonly struct carrying a value and a status code side by side. `TStatus` is a small `byte` enum defined per subsystem — by convention `0` always means success. `IsSuccess`/`IsFailure` check the status via a raw byte comparison (no boxing, no virtual dispatch), and `Value`/`Status` are public readonly fields — no throwing getter, so there's no extra branch when you access `Value` after already checking `IsSuccess`. Each consuming subsystem defines its own status enum with only the outcomes that method can actually produce, so the signature itself documents every possible result. Actual errors (corruption, I/O failure, bounds violations) are never encoded this way — those still throw.

## 💻 Usage

Typhon's own hot paths follow this pattern internally — B+Tree key lookups return `Result<int, BTreeLookupStatus>`, and MVCC revision-chain reads return `Result<ComponentInfo.CompRevInfo, RevisionReadStatus>` with four distinct outcomes:

```csharp
public enum RevisionReadStatus : byte
{
    Success = 0,           // revision found and visible at this snapshot tick
    NotFound = 1,          // entity has no revision chain (never created)
    SnapshotInvisible = 2, // revision exists but isn't visible at the reader's tick
    Deleted = 3,           // entity was tombstoned at or before the reader's tick
}

Result<ComponentInfo.CompRevInfo, RevisionReadStatus> result = GetCompRevInfoFromIndex(pk, info, tick);
if (result.IsSuccess)
{
    var compRevInfo = result.Value; // direct field access — no throwing getter
    // ... use compRevInfo
}
else
{
    switch (result.Status)
    {
        case RevisionReadStatus.NotFound: break;          // never existed
        case RevisionReadStatus.SnapshotInvisible: break;  // not in our snapshot yet
        case RevisionReadStatus.Deleted: break;             // tombstoned
    }
}
```

The same `Result<TValue, TStatus>` struct is `public` and generic, so your own performance-critical code can adopt the identical pattern: define a small `byte` enum for your method's expected outcomes (`0` = success) and return `Result<YourValue, YourStatusEnum>` instead of `bool` + `out` or an exception.

## ⚠️ Guarantees & limits

- Zero heap allocation in both the success and failure case — `TValue : unmanaged` and `TStatus : unmanaged, Enum` keep the whole struct blittable.
- Benchmark-validated against `bool` + `out`: no measurable overhead (780 ns vs 785 ns for 64 lookups — noise-level difference). Status-enum `switch` compiles to a jump table.
- `Value` and `Status` are plain public fields, not validated properties — accessing `Value` without checking `IsSuccess` first silently returns `default(TValue)`, not an exception. Same discipline as `Nullable<T>.Value` misuse, but without any guard.
- Convention, not compiler-enforced: status `0` must mean success in every `TStatus` enum you define. `IsSuccess` assumes this.
- Struct size follows Typhon's even-sized hot-path-struct rule (ADR-027) — e.g. `Result<int, BTreeLookupStatus>` is 8 bytes total, cache-friendly and division-friendly.
- Reserved for *expected* non-error outcomes only. Corruption, I/O failure, and bounds violations remain exceptions — `Result` is not a general error-handling replacement.

## 🧪 Tests

- [ResultTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Errors/ResultTests.cs) — success/failure construction, `IsSuccess`/`IsFailure`, default-value-on-failure `Value` access (the "misuse without checking `IsSuccess` first" case), and status-enum round-tripping for `BTreeLookupStatus`/`RevisionReadStatus`.

## 🔗 Related

- Related catalog entry: [TyphonException Hierarchy & Catalog](./exception-hierarchy.md) — contrasts `Result` (expected outcomes) against exceptions (actual errors)

<!-- Deep dive: claude/design/Errors/04-result-type.md -->
<!-- Overview: claude/overview/10-errors.md § Result Types for Hot Paths -->
<!-- ADR: claude/adr/027-even-sized-hot-path-structs.md -->

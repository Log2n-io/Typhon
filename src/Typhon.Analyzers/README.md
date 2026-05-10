# Typhon.Analyzers

Custom Roslyn analyzers for the Typhon database engine project.

## Analyzer Summary

| ID | Severity | Description |
|----|----------|-------------|
| TYPHON001 | Error | `[NoCopy]` type parameters must use `ref` modifier |
| TYPHON003 | Error | `[NoCopy]` type must not be copied |
| TYPHON004 | Error | IDisposable result must be disposed |
| TYPHON005 | Error | Type with critical disposable field must implement IDisposable |
| TYPHON006 | Error | Dispose() must dispose all critical fields |
| TYPHON007 | Error | Early return in Dispose() must not skip critical field disposal |
| TYPHON008 | Error | Public-namespace type exposes internal-namespace type on its public surface |

---

## NoCopyAnalyzer (TYPHON001 + TYPHON003)

A unified analyzer that protects types marked with `[NoCopy]` from value copies.

### The `[NoCopy]` Attribute

Apply `[NoCopy]` to any large struct that must be passed by `ref` to avoid expensive stack copies:

```csharp
[NoCopy(Reason = "~248 byte struct with mutable SIMD cache and epoch-pinned pages")]
[StructLayout(LayoutKind.Sequential)]
public unsafe struct ChunkAccessor : IDisposable { /* ... */ }
```

The optional `Reason` property is included in diagnostic messages to explain *why* the type must not be copied.

---

### TYPHON001 — ref Parameter Enforcement

**Severity:** Error

Parameters of `[NoCopy]` types must always use the `ref` modifier. The `in`, `out`, and by-value modifiers are rejected.

**Why:** Large structs with mutating methods suffer from:
- **By-value:** Expensive stack copies (~248+ bytes per call)
- **`in` modifier:** Defensive copies when calling non-readonly methods, completely defeating the performance design
- **Cache pollution:** Large stack copies pollute CPU caches

#### Correct Usage

```csharp
// CORRECT - Pass by ref
public void ProcessChunk(ref ChunkAccessor accessor)
{
    ref var chunk = ref accessor.GetChunk<MyData>(chunkId);
}
```

#### Incorrect Usage

```csharp
// ERROR TYPHON001 - Missing ref modifier
public void ProcessChunk(ChunkAccessor accessor) { }

// ERROR TYPHON001 - 'in' causes defensive copies
public void ProcessChunk(in ChunkAccessor accessor) { }
```

#### Code Fix

The analyzer includes an automatic code fix. In Visual Studio or Rider:
1. Place cursor on the error
2. Press `Ctrl+.` (or `Alt+Enter` in Rider)
3. Select "Add 'ref' modifier" or "Replace 'in' with 'ref'"

---

### TYPHON003 — No-Copy Enforcement

**Severity:** Error

Detects value copies of `[NoCopy]` types through assignments, variable declarations, and return statements.

**Why:** Copying duplicates internal mutable state (caches, epoch pins, dirty flags), leading to:
- Expensive stack copies
- Double-dispose or inconsistent state
- Subtle correctness bugs

#### Detected Patterns

```csharp
// ERROR TYPHON003 - Copying via assignment
var copy = existingAccessor;

// ERROR TYPHON003 - Copying via return (field/parameter)
return _cachedAccessor;
```

#### Allowed Patterns

```csharp
// CORRECT - Create via factory method
using var accessor = segment.CreateChunkAccessor();

// CORRECT - Pass by ref
ProcessData(ref accessor);

// CORRECT - Ref local (no copy)
ref var alias = ref accessor;

// CORRECT - Ref reassignment (no copy)
alias = ref otherAccessor;

// CORRECT - default/new expressions
var fresh = default(ChunkAccessor);

// CORRECT - Return from factory (local variable initialized from allowed value)
ChunkAccessor CreateAccessor()
{
    var a = segment.CreateChunkAccessor();
    return a; // OK — local was initialized from invocation
}
```

---

## DisposableNotDisposedAnalyzer (TYPHON004)

**Severity:** Error

**Description:** Detects IDisposable instances returned from method calls that are not properly disposed.

### Why This Rule Exists

Failing to dispose IDisposable resources causes resource leaks. In Typhon, this is especially critical:

| Type | Consequence if not disposed |
|------|----------------------------|
| `ChunkAccessor` | **Page cache exhaustion** — epoch-pinned pages prevent eviction indefinitely |
| `Transaction` | **Data corruption** - uncommitted changes leak, resource exhaustion |

This analyzer addresses limitations in CA2000 which lacks inter-procedural analysis, misses tuple returns, and ignores exception flow paths.

### Detected Patterns

```csharp
// ERROR TYPHON004 - Discarded result
CreateTransaction();

// ERROR TYPHON004 - Explicitly discarded
_ = CreateTransaction();

// ERROR TYPHON004 - Variable never disposed
var t = CreateTransaction();
t.DoWork();
// end of method without dispose

// ERROR TYPHON004 - Reassignment without disposing first
var t = CreateTransaction();
t = CreateTransaction();  // First value leaked!
t.Dispose();
```

### Correct Usage

```csharp
// CORRECT - Using declaration (preferred)
using var t = CreateTransaction();
t.DoWork();
// Automatically disposed at end of scope

// CORRECT - Using statement
using (var t = CreateTransaction())
{
    t.DoWork();
}

// CORRECT - Explicit Dispose() call
var t = CreateTransaction();
try
{
    t.DoWork();
}
finally
{
    t.Dispose();
}

// CORRECT - Return transfers ownership to caller
public Transaction CreateAndReturn()
{
    return CreateTransaction();
}

// CORRECT - Field assignment transfers ownership
private Transaction _transaction;
public void Initialize()
{
    _transaction = CreateTransaction();
}
```

### Code Fix

The analyzer includes automatic code fixes. In Visual Studio or Rider:
1. Place cursor on the error
2. Press `Ctrl+.` (or `Alt+Enter` in Rider)
3. Select one of:
   - **"Add 'using' declaration"** - Converts `var x = Method();` to `using var x = Method();`
   - **"Add Dispose() call"** - Adds `x.Dispose();` at end of the containing block

---

## CriticalFieldDisposalAnalyzer (TYPHON005 + TYPHON006 + TYPHON007)

A unified analyzer that ensures types owning critical disposable fields handle their lifecycle correctly.
It performs a single pass per named type and emits up to three diagnostics, forming a defense-in-depth chain:

```
TYPHON005: Does the container implement IDisposable?
    └─ yes ─→ TYPHON006: Does Dispose() cover ALL critical fields?
                 └─ yes ─→ TYPHON007: Do early returns skip any disposal?
```

### Critical Types

Both this analyzer and TYPHON004 share the same critical type list via `DisposableAnalyzerHelpers`:

| Type | Consequence if not disposed |
|------|----------------------------|
| `ChunkAccessor` | Page cache deadlock |
| `Transaction` | Uncommitted changes and resource leak |

---

### TYPHON005 — Container Must Implement IDisposable

**Severity:** Error

Types that hold a field of a critical disposable type must implement `IDisposable`.

**Smart exclusions** (not flagged):
- `ref struct` types (can't implement interfaces; typically short-lived with explicit disposal)
- Inline arrays (`[InlineArray]` — compiler-generated, disposal managed by containing struct)
- Types nested inside an `IDisposable` parent (parent handles disposal)
- Types with an explicit disposal method (e.g., `DisposeAccessors()`) that disposes all critical fields

```csharp
// ERROR TYPHON005 - Contains ChunkAccessor but not IDisposable
public class DataHolder
{
    private ChunkAccessor _accessor;
}

// CORRECT - Implements IDisposable
public class DataHolder : IDisposable
{
    private ChunkAccessor _accessor;
    public void Dispose() => _accessor.Dispose();
}
```

---

### TYPHON006 — Dispose() Must Be Complete

**Severity:** Error

If a type implements `IDisposable` and has critical fields, its `Dispose()` method must dispose every one of them.

**Recognized disposal patterns:**
- Direct: `_field.Dispose()`
- Null-conditional: `_field?.Dispose()`
- Via `this`: `this._field.Dispose()`
- Via local assignment: `var x = _field; x.Dispose();`
- Via `foreach` iteration: `foreach (var item in _collection) { item.Dispose(); }`
- Via `Dictionary.Values`: `foreach (var v in _dict.Values) { v.Dispose(); }`

```csharp
// ERROR TYPHON006 - Dispose() forgets _revisionAccessor
public class MyTable : IDisposable
{
    private ChunkAccessor _dataAccessor;
    private ChunkAccessor _revisionAccessor;

    public void Dispose()
    {
        _dataAccessor.Dispose();
        // _revisionAccessor.Dispose() is missing!
    }
}
```

---

### TYPHON007 — Early Returns Must Not Skip Disposal

**Severity:** Error

Early `return` statements inside `Dispose()` must not bypass critical field disposal.

```csharp
// ERROR TYPHON007 - Early return skips _accessor.Dispose()
public void Dispose()
{
    if (!IsValid)
    {
        return;  // BUG: _accessor never disposed on this path!
    }
    _accessor.Dispose();
}

// CORRECT - Dispose before early return
public void Dispose()
{
    if (!IsValid)
    {
        _accessor.Dispose();
        return;
    }
    _accessor.Dispose();
}
```

---

## InternalApiLeakAnalyzer (TYPHON008)

**Severity:** Error

Enforces the namespace boundary defined in `claude/research/PublicVsInternalApiClassification.md`: types in the **public** namespace `Typhon.Engine` must not expose types from the **internal** namespace `Typhon.Engine.Internals` on their public/protected surface.

### Why This Rule Exists

Once the big-bang namespace migration lands, the Typhon engine assembly has exactly two namespaces:
- `Typhon.Engine` — the consumer-facing public surface (~181 types)
- `Typhon.Engine.Internals` — implementation details, exposed only to friend assemblies via `InternalsVisibleTo` (~424 types)

A public type may freely **use** internal types in its implementation, but it must not **mention** them on its own public surface — doing so re-exports the internal type to consumers and silently re-grows the public API behind the namespace split's back.

Pre-migration the analyzer is dormant (no type lives in `Typhon.Engine.Internals` yet). Post-migration it becomes a build-time guard against accidental drift.

### Detected Surfaces

For every externally visible type in `namespace Typhon.Engine`, the following surfaces are checked for references to `Typhon.Engine.Internals` types:

- Base type and implemented interfaces
- Field types (public/protected fields)
- Property types and indexer parameter types
- Method return types and parameter types
- Event types
- Generic type arguments inside any of the above (recursive — e.g. `List<Internals.Foo>` is a leak)
- Generic type-parameter constraints on the type or its methods
- Array element types and pointer pointed-at types

Generated code (source-generator output, files matching `.g.cs` / `.generated.cs`, members marked `[CompilerGenerated]` / `[GeneratedCode]`, and implicitly declared symbols) is excluded — leaks in generated code are the producer's responsibility, not the developer's.

### Example

```csharp
namespace Typhon.Engine;

public class WalManager  // public — consumer surface
{
    // ERROR TYPHON008 — exposes internal WalSegment on a public field
    public Internals.WalSegment ActiveSegment;

    // ERROR TYPHON008 — return type leaks
    public Internals.WalSegment OpenSegment(int id) => /* ... */;

    // OK — internal type used in implementation, not on the surface
    private Internals.WalSegment _current;
    public void Flush() => _current.Flush();
}
```

### Resolution

When the analyzer fires, choose one of:
1. **Promote the internal type** to `Typhon.Engine` — appropriate when it really is part of the consumer contract (and was misclassified as internal).
2. **Hide the leak behind a public-only abstraction** — return an interface, a snapshot struct, or a public wrapper over the internal type.
3. **Make the public type internal** — appropriate when the public type was misclassified and is itself an implementation detail.

---

## Adding to Other Projects

To enable these analyzers in additional projects, add to the `.csproj` file:

```xml
<ItemGroup>
  <!-- Reference the Roslyn analyzers for Typhon enforcement -->
  <ProjectReference Include="path\to\Typhon.Analyzers\Typhon.Analyzers.csproj"
                    ReferenceOutputAssembly="false"
                    OutputItemType="Analyzer" />
</ItemGroup>
```

## Technical Details

- **Target Framework:** netstandard2.0 (compatible with all modern .NET versions)
- **Dependencies:**
  - Microsoft.CodeAnalysis.CSharp 5.0.0
  - Microsoft.CodeAnalysis.CSharp.Workspaces 5.0.0
  - Microsoft.CodeAnalysis.Analyzers 3.11.0

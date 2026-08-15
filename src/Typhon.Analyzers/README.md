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
| TYPHON009 | Warning | Spatial component mutated via `GetSpan`/`Get` instead of the `WriteSpatial` barrier |
| TYPHON010 | Warning | Component struct stores padding beyond a 4-byte multiple — every entity pays for bytes that carry no field |
| TYPHON011 | Error | Component struct's managed and marshalled layouts differ (`bool` / `char`) — the schema cannot describe it correctly |

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

## ComponentLayoutAnalyzer (TYPHON010 + TYPHON011)

### TYPHON010 — Avoidable padding

**Severity:** Warning

Reports a `[Component]` struct that stores more padding than a 4-byte-aligned layout of its fields would need.

### Why This Rule Exists

A component column is strided by `sizeof(T)` — that is the stride `Span<T>` and `ref T` step by, and the engine takes it as the component's storage size
(`DBComponentDefinition.Build`, rule SCHEMA-06). Padding is therefore stored per entity, written to the WAL and checkpointed, while carrying no field:

```csharp
[Component("Ship.Vitals", 1)]
public struct Vitals { public long Owner; public int Hp; }   // 16 bytes stored for 12 bytes of data
```

Before #816 the engine strided such a column by the *field extent* instead, which silently mis-addressed every slot after the first. Taking `sizeof(T)` fixed
that; this analyzer is what keeps the resulting cost visible.

### The baseline is `Pack = 4`, rounded up to 4 — not the end of the last field

Rounding a component up to 4 costs at most 3 bytes and keeps every layout word-aligned. Rounding up to **8** — which a single `long` or `double` imposes on the
whole struct — costs up to 7, and is the waste worth acting on.

The comparison is against **what the struct would measure at `Pack = 4`**, not against where its last field ends. Measuring the tail would make the diagnostic a
function of field *order* rather than of wasted storage:

| Struct | Type size | Packs to | Reported? |
|---|---|---|---|
| `{ long; int }` | 16 | 12 | **yes** — 4 bytes recoverable |
| `{ int; long }` | 16 | 12 | **yes** — same waste, padding just sits in the middle |
| `{ AABB2F; byte }` | 20 | 17 → 20 | no — already a 4-byte multiple |
| `{ int }` with `Size = 8` | 8 | 4 | **yes** — declared oversize |

### Resolution

Add `Pack = 4`, which caps every field's alignment at 4:

```csharp
[Component("Ship.Vitals", 1)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct Vitals { public long Owner; public int Hp; }   // 12 bytes stored
```

**Reordering the fields does not help** — it relocates the padding rather than removing it (`{ int; long }` is 16 bytes as well).

### `Pack` or `Size`?

Prefer **`Pack`**: it follows the fields, so adding one adjusts the layout, where a pinned `Size` freezes a number that rots — too small becomes a runtime
`TypeLoadException`, too large silently re-introduces the padding.

Use **`Size`** when the component already has data on disk. `Pack` caps alignment for *every* field, so it can move **interior** offsets — `{ int A; long B;
float C }` at `Pack = 4` slides `B` from 8 to 4 — and field offsets are persisted (`FieldR1.Offset`) and compared by `SchemaValidator`. `Size` pinned to the
field extent never moves anything. For system components this is the difference between a transparent change and a `BK_SystemSchemaRevision` bump that refuses
every existing database (rule SCHEMA-05); `ArchetypeR1` and `SchemaHistoryR1` were verified offset-by-offset before taking `Pack = 4`.

The shared trade is alignment: at a 12-byte stride the `long` is 4-byte-aligned on odd slots. Fine for ordinary loads on x64 and arm64, but such a field must
not be the target of `Interlocked`. When the padding is worth paying for, suppress the diagnostic at the declaration and say why.

### Limits

The analyzer reimplements sequential struct layout: primitives, enums, nested structs, fixed-size buffers, `[InlineArray]`, `Pack`, `Size` and `CharSet`.
Anything it cannot model exactly makes it stay silent rather than report a byte count it cannot stand behind — `LayoutKind.Explicit` or `Auto`, a pointer or
`nint` field, a generic component, a type it does not recognise, and nesting deeper than 8 levels. A self-referential struct (only expressible in invalid
source, but analyzers run on that too) is detected and abandoned rather than recursed into.

---

### TYPHON011 — Managed vs marshalled layout

**Severity:** Error

Reports a `[Component]` struct whose **managed** layout differs from its **marshalled** one.

#### Why This Rule Exists

The reflection schema path records field offsets with `Marshal.OffsetOf` (`DatabaseDefinitions.ReflectComponentSpec`), which describes the *marshalled*
layout, while every accessor reads the component through the *managed* one. They agree for ordinary blittable primitives and diverge for exactly two types —
`bool` marshals to a 4-byte Win32 `BOOL` (1 byte managed), `char` to a 1-byte ANSI character (2 bytes managed):

```
struct { bool A; bool B; int C; }    managed  A@0 B@1 C@4  (8 bytes)
                                     marshal  A@0 B@4 C@8  (12 bytes)

struct { char A; char B; int C; }    managed  A@0 B@2 C@4  (8 bytes)
                                     marshal  A@0 B@1 C@4  (8 bytes)   ← same size, wrong offsets
```

When they diverge, every field-addressed path reads the wrong bytes with no error anywhere: index key extraction, WAL field decode, crash recovery, schema
evolution, the integrity scanner, the Workbench raw read. Whole-struct copies keep working, which is what makes it so quiet.

The `char` row is why this is a compile-time check and not a registration assert: both layouts are 8 bytes, so no runtime size comparison can see it.

#### Resolution

Use `byte` for a flag and `ushort` for a code unit. This is an **error**, not a warning, because there is no correct way to use such a component.

It is a *comparison*, not a ban on `bool`/`char`, and stays silent whenever the two layouts happen to agree:

- a single `bool` before a wider field — the managed layout pads it out to the same place the marshalled one puts it, so the common one-flag shape is fine;
- `CharSet.Unicode` on the struct — `char` then marshals as 2 bytes, the same as managed;
- an explicit `[MarshalAs]` on a field — the marshalled width is the author's to define, so the check abandons rather than accusing an annotated field.

Divergence hidden inside a nested struct is caught too, including when it sits last and displaces nothing: the check compares each field's *width* as well as
its offset.

#### Note on the generated path

The source generator does **not** use `Marshal.OffsetOf` — since #816 it measures offsets against a stack probe with `Unsafe.ByteOffset`, so a generated spec
already carries managed offsets. This diagnostic protects the reflection fallback (`CreateFromAccessor(Type)`, `AssemblySchemaLoader`), which has only a
`Type` to work from and cannot do the same.

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

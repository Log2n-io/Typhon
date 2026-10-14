using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Typhon.Analyzers;

/// <summary>
/// Guards the two ways a <c>[Component]</c> struct's declared shape can disagree with the storage Typhon lays out for it.
///
/// <para>
/// A component column is strided by <c>sizeof(T)</c> and its fields are addressed by the offsets recorded in the schema, because that is what
/// <c>Span&lt;T&gt;</c>, <c>ref T</c> and every field-aware path step through (#816, rule SCHEMA-06). Two things can therefore go wrong at declaration time:
/// the struct can be bigger than its fields need, and the layout the schema records can be a different layout from the one the accessors read.
/// </para>
///
/// <list type="bullet">
///   <item><b>TYPHON010</b> (warning) — the struct stores padding that <c>Pack = 4</c> would remove; every entity pays for bytes that carry no field.</item>
///   <item><b>TYPHON011</b> (error) — the struct's managed and marshalled layouts differ, so the schema cannot describe it correctly.</item>
///   <item><b>TYPHON013</b> (warning) — the struct is too large to travel in a <c>ComponentValue</c>'s inline payload, so it cannot be passed to <c>Spawn</c>.</item>
/// </list>
///
/// <para>
/// <b>TYPHON010</b> compares the struct against what it would measure at <c>Pack = 4</c>, rounded up to a 4-byte multiple. Comparing against the end of the
/// last field instead would see only TAIL padding, which is an artefact of field ORDER rather than of the type: <c>{ long; int }</c> and <c>{ int; long }</c>
/// are both 16 bytes carrying 12 bytes of payload, and both pack to 12, but only the first has its padding at the end. Rounding up to 4 is accepted and never
/// reported — it costs at most 3 bytes and keeps the layout word-aligned, whereas the 8-byte rounding one <c>long</c> or <c>double</c> imposes costs up to 7.
/// </para>
///
/// <para>
/// <b>TYPHON011.</b> The reflection schema path reads offsets with <c>Marshal.OffsetOf</c>, which describes the MARSHALLED layout, while the accessors read
/// the MANAGED one. They agree for ordinary blittable primitives, and diverge for <c>bool</c> (4 bytes marshalled under the default marshalling, 1 managed)
/// and <c>char</c> (1 under <c>CharSet.Ansi</c>, 2 managed):
/// <code>
///   struct { bool A; bool B; int C; }    managed  A@0 B@1 C@4  (8 bytes)
///                                        marshal  A@0 B@4 C@8  (12 bytes)
///   struct { char A; char B; int C; }    managed  A@0 B@2 C@4  (8 bytes)
///                                        marshal  A@0 B@1 C@4  (8 bytes)   ← same size, wrong offsets
/// </code>
/// The <c>char</c> case is why this is a compile-time check rather than a size assertion at registration: the totals match, so nothing at runtime can see it.
/// Every field-addressed path — index key extraction, WAL field decode, crash recovery, schema evolution, the integrity scanner, the Workbench raw read —
/// would then read the wrong bytes, silently. Use <c>byte</c> for a flag and <c>ushort</c> for a code unit.
/// </para>
///
/// <para>
/// The rule is a comparison rather than a ban on <c>bool</c>/<c>char</c> because the two layouts often agree anyway — a lone <c>bool</c> before an <c>int</c>
/// sits at the same offset either way — and because <c>CharSet.Unicode</c> or an explicit <c>[MarshalAs]</c> can reconcile them deliberately.
/// </para>
///
/// <para>
/// <b>TYPHON013</b> reports a struct past <see cref="InlineSpawnPayloadBytes"/>, the payload a <c>ComponentValue</c> carries inline on the spawn path. It is a
/// WARNING and not an error, because such a component is perfectly legal — it just cannot be handed to <c>Spawn</c>; the caller spawns the entity with its
/// smaller components and writes the large one through <c>OpenMut</c> afterwards, in the same transaction. A component that deliberately takes that route
/// suppresses this, and the suppression is then the only place that decision is written down.
/// </para>
///
/// <para>
/// <b>It exists because the runtime failure used to be silent, and is still late.</b> <c>ComponentValue.Create&lt;T&gt;</c> copies <c>sizeof(T)</c> bytes into a
/// fixed buffer of exactly this size, and its bounds check sat behind <c>CheckConfig.Enabled</c> — false by default, no runtime setter — so an oversized
/// component wrote past the buffer and killed the process with no exception, no message and no stack (#1140, found from a 132-byte component). That check is
/// ungated now, so the failure is loud; but it still arrives on the first spawn at run time, whereas the size is a fact about the declaration and is knowable
/// here. The layout walk this analyzer already performs for TYPHON010 is the whole cost.
/// </para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class ComponentLayoutAnalyzer : DiagnosticAnalyzer
{
    public const string PaddingDiagnosticId = "TYPHON010";
    public const string MarshalDivergenceDiagnosticId = "TYPHON011";
    public const string InlineSpawnPayloadDiagnosticId = "TYPHON013";

    /// <summary>
    /// Bytes a <c>ComponentValue</c> carries inline on the spawn path — <c>Typhon.Engine</c>'s <c>ComponentValue.MaxPayloadSize</c>.
    /// </summary>
    /// <remarks>
    /// <b>Duplicated, necessarily, and pinned by a test.</b> This project targets <c>netstandard2.0</c> and must not reference the assembly it analyses, so the
    /// number cannot be read from its owner. <c>ComponentLayoutAnalyzerTests.TheInlinePayloadConstantMatchesTheEngines</c> asserts the two are equal, because a
    /// duplicated constant with nothing holding the halves together is one that rots — and this one rotting means the analyzer either nags about components
    /// that fit, or stays quiet about ones that do not.
    /// </remarks>
    public const int InlineSpawnPayloadBytes = 112;

    private const string ComponentAttributeFqn = "Typhon.Schema.Definition.ComponentAttribute";
    private const string StructLayoutAttributeFqn = "System.Runtime.InteropServices.StructLayoutAttribute";
    private const string MarshalAsAttributeFqn = "System.Runtime.InteropServices.MarshalAsAttribute";
    private const string InlineArrayAttributeFqn = "System.Runtime.CompilerServices.InlineArrayAttribute";

    /// <summary>Default <c>Pack</c> on the platforms Typhon targets (x64 / arm64). <c>Pack = 0</c> on the attribute means "platform default".</summary>
    private const int DefaultPack = 8;

    /// <summary>
    /// The granularity a component's storage is allowed to round up to, and the <c>Pack</c> the diagnostic recommends. Rounding to 4 costs at most 3 bytes per
    /// entity and keeps every layout word-aligned; rounding to 8 — what one <c>long</c> or <c>double</c> field imposes on the whole struct — costs up to 7.
    /// </summary>
    private const int AcceptedGranularity = 4;

    /// <summary>
    /// Recursion cap for nested value types. Analyzers run on INVALID source too, where a struct can legally-in-the-symbol-graph contain itself
    /// (<c>struct C { C Self; }</c> is CS0523, but the symbol still exists), and an unbounded walk of that is a stack overflow that takes the compiler or the
    /// IDE down with it. The cycle set below catches the direct case; this bounds everything else.
    /// </summary>
    private const int MaxNestedFieldDepth = 8;

    private static readonly DiagnosticDescriptor PaddingRule = new DiagnosticDescriptor(
        PaddingDiagnosticId,
        "Component struct stores avoidable padding",
        "Component '{0}' is {1} bytes but packs to {2} — every entity stores {3} bytes of avoidable padding. {4}.",
        "Performance",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "A component column is strided by sizeof(T), so padding the compiler adds for alignment is stored per entity, logged to the WAL and checkpointed, "
          + "while carrying no field. A single long or double rounds the whole struct up to a multiple of 8, which is the waste this reports; rounding up to 4 "
          + "is accepted and never reported. Reordering the fields relocates the padding rather than removing it — Pack is what removes it, and is preferred "
          + "over Size because it follows the fields instead of freezing a number that rots when one is added. The trade is alignment: an 8-byte field in a "
          + "12-byte stride is 4-byte-aligned on odd slots, fine for ordinary loads but not for Interlocked. Pack also moves interior field offsets, which are "
          + "persisted — prefer Size on a component that already has data on disk.");

    private static readonly DiagnosticDescriptor MarshalDivergenceRule = new DiagnosticDescriptor(
        MarshalDivergenceDiagnosticId,
        "Component struct's managed and marshalled layouts differ",
        "Component '{0}' cannot be laid out correctly: field '{1}' occupies {2} at offset {3} in the managed layout but {4} at offset {5} in the marshalled "
      + "one. A bool marshals to 4 bytes (1 managed) and a char to 1 (2 managed) — use byte for a flag and ushort for a code unit.",
        "Correctness",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "Typhon's reflection schema path records field offsets with Marshal.OffsetOf, which describes the marshalled layout, while every accessor reads "
          + "the component through the managed one. The two agree for ordinary blittable primitives and diverge for bool and char. When they diverge, every "
          + "field-addressed path — index key extraction, WAL field decode, crash recovery, schema evolution, the integrity scanner, the Workbench raw read — "
          + "reads the wrong bytes, with no error anywhere; whole-struct copies keep working, which is what makes it so quiet. For char the two layouts can "
          + "even have the same total size, so no runtime size check can detect it. This is an error rather than a warning because there is no correct way to "
          + "use such a component.");

    private static readonly DiagnosticDescriptor InlineSpawnPayloadRule = new DiagnosticDescriptor(
        InlineSpawnPayloadDiagnosticId,
        "Component struct is too large to pass to Spawn",
        "Component '{0}' is {1} bytes, past the {2} a ComponentValue carries inline, so it cannot be passed to Spawn. Split it — a large key and its payload "
      + "are usually two components, and splitting also keeps the bigger half out of every read that only wanted the smaller — or spawn the entity without "
      + "it and write it through OpenMut in the same transaction.",
        "Correctness",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "Spawn takes its component values as ComponentValue, a 128-byte struct whose inline payload is 112 bytes, so a larger component cannot travel that "
          + "way. This is reported at the declaration because that is where it can still be acted on: the author can split the component, which is usually the "
          + "better layout anyway, while at the call site the only remaining option is the two-step spawn. A component over the limit is LEGAL - spawn the "
          + "entity with its smaller components and write this one through OpenMut - so this is a warning, and a component taking that route deliberately should "
          + "suppress it with a reason. The runtime check used to be compiled out by default and corrupted memory instead of throwing (#1140); it is ungated "
          + "now, but it still fires on the first spawn, whereas the size is a property of the declaration.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(PaddingRule, MarshalDivergenceRule, InlineSpawnPayloadRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeNamedType, SymbolKind.NamedType);
    }

    /// <summary>How a field's width is measured: as the runtime lays it out, or as the interop marshaller would.</summary>
    private enum LayoutMode
    {
        Managed,
        Marshalled,
    }

    /// <summary>Where one field of the struct under analysis landed, and how wide it is. Both halves are compared between the two layouts.</summary>
    private readonly struct FieldPlacement
    {
        public FieldPlacement(IFieldSymbol field, int offset, int size)
        {
            Field = field;
            Offset = offset;
            Size = size;
        }

        public readonly IFieldSymbol Field;
        public readonly int Offset;
        public readonly int Size;
    }

    private static void AnalyzeNamedType(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.TypeKind != TypeKind.Struct || type.IsGenericType || !HasComponentAttribute(type))
        {
            return;
        }

        // Everything below is a layout computation that must match the runtime's. Anything it cannot account for exactly (explicit layout, a field type it does
        // not model) makes the byte counts a guess — and a diagnostic naming a wrong byte count is worse than none, so bail rather than approximate.
        var managed = new List<FieldPlacement>();
        if (!TryComputeLayout(type, LayoutMode.Managed, packOverride: 0, managed, out var managedLayout) || managedLayout.LastFieldEnd == 0)
        {
            return;
        }

        // TYPHON013 FIRST, and it does not return, because it is the only one of the three that is a correctness bound rather than advice: a component past the
        // inline payload cannot be passed to Spawn at all. It was reported last, after the TYPHON011 block below — which RETURNS — so a component that was both
        // marshal-divergent and oversized reported only the divergence, and fixing that revealed a second diagnostic the analyzer had already computed. The
        // three are independent remedies: splitting an oversized component does not remove padding from either half, Pack almost never brings 132 bytes under
        // 112, and neither has anything to do with a managed/marshalled disagreement.
        if (managedLayout.Size > InlineSpawnPayloadBytes)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                InlineSpawnPayloadRule, type.Locations[0], type.Name, managedLayout.Size, InlineSpawnPayloadBytes));
        }

        // TYPHON011 next: when the two layouts disagree the component is unusable, and the padding advice would be advice about the wrong layout.
        var marshalled = new List<FieldPlacement>();
        if (TryComputeLayout(type, LayoutMode.Marshalled, packOverride: 0, marshalled, out var marshalledLayout) && marshalled.Count == managed.Count)
        {
            // A field being WIDER when marshalled is not by itself a problem — a lone `bool` before an `int` occupies 1 byte managed and 4 marshalled, yet the
            // int lands at 4 either way and the struct is 8 either way, so the schema describes it correctly. What matters is whether any field ends up at a
            // different OFFSET, or whether the struct as a whole ends up a different SIZE — the latter being how a divergence inside the LAST field shows up,
            // since there is nothing after it to displace.
            var culprit = -1;
            for (var i = 0; i < managed.Count && culprit < 0; i++)
            {
                if (managed[i].Offset != marshalled[i].Offset)
                {
                    culprit = i;
                }
            }

            if (culprit < 0 && managedLayout.Size != marshalledLayout.Size)
            {
                for (var i = 0; i < managed.Count && culprit < 0; i++)
                {
                    if (managed[i].Size != marshalled[i].Size)
                    {
                        culprit = i;
                    }
                }
            }

            if (culprit >= 0)
            {
                var field = managed[culprit].Field;
                var location = field.Locations.Length > 0 ? field.Locations[0] : type.Locations[0];
                context.ReportDiagnostic(Diagnostic.Create(
                    MarshalDivergenceRule, location, type.Name, field.Name,
                    managed[culprit].Size, managed[culprit].Offset, marshalled[culprit].Size, marshalled[culprit].Offset));
                return;
            }
        }

        // What the struct would measure at the recommended Pack, which is the only honest baseline: comparing against the end of the last field would report
        // TAIL padding, and whether padding lands at the tail or in the middle is a fact about field order, not about how much storage is being wasted.
        if (!TryComputeLayout(type, LayoutMode.Managed, packOverride: AcceptedGranularity, null, out var packedLayout))
        {
            return;
        }

        var accepted = AlignUp(packedLayout.Size, AcceptedGranularity);
        if (managedLayout.Size <= accepted)
        {
            return;
        }

        // Pack cannot shrink a struct whose size the author declared outright — the runtime takes the declared value verbatim — so in that case the remedy is
        // the declaration itself.
        var remedy = managedLayout.DeclaredSize > 0
            ? $"Lower Size to {packedLayout.Size}, or drop it and add Pack = {AcceptedGranularity}"
            : $"Add [StructLayout(LayoutKind.Sequential, Pack = {AcceptedGranularity})]";

        context.ReportDiagnostic(Diagnostic.Create(
            PaddingRule, type.Locations[0], type.Name, managedLayout.Size, accepted, managedLayout.Size - accepted, remedy));
    }

    private static bool HasComponentAttribute(ISymbol symbol)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() == ComponentAttributeFqn)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The measured shape of one type.</summary>
    private struct Layout
    {
        public int Size;
        public int Alignment;
        public int LastFieldEnd;
        public int DeclaredSize;
    }

    private static bool TryComputeLayout(ITypeSymbol type, LayoutMode mode, int packOverride, List<FieldPlacement> placements, out Layout layout)
        => TryComputeLayout(type, mode, packOverride, placements, depth: 0, visiting: null, out layout);

    /// <summary>
    /// Computes a type's size and alignment, plus where its last field ends. Mirrors sequential layout: each field is placed at the next offset aligned to
    /// <c>min(alignof(field), Pack)</c>, and the total is rounded up to the widest such alignment — unless <c>StructLayout.Size</c> is declared, which the
    /// runtime takes verbatim in both directions (it shrinks a struct as readily as it grows one).
    /// </summary>
    /// <param name="mode">Which width table to use for <c>bool</c> and <c>char</c> — the only two types where managed and marshalled disagree.</param>
    /// <param name="packOverride">When non-zero, replaces the type's own <c>Pack</c> at THIS level only; nested types keep theirs, which is what declaring
    /// <c>Pack</c> on the component itself would actually do.</param>
    /// <param name="placements">Receives each instance field with its offset and width; null to skip collecting them.</param>
    /// <returns><c>false</c> when any part of the layout cannot be modelled exactly.</returns>
    private static bool TryComputeLayout(ITypeSymbol type, LayoutMode mode, int packOverride, List<FieldPlacement> placements,
        int depth, HashSet<ISymbol> visiting, out Layout layout)
    {
        layout = default;
        layout.Alignment = 1;

        if (depth > MaxNestedFieldDepth)
        {
            return false;
        }

        if (TryGetPrimitiveSize(type, mode, out var primitiveSize))
        {
            layout.Size = primitiveSize;
            layout.Alignment = primitiveSize;
            layout.LastFieldEnd = primitiveSize;
            return true;
        }

        if (type is not INamedTypeSymbol named)
        {
            return false;
        }

        // An enum is not TypeKind.Struct, so this must precede the struct check or every component with an enum field bails out silently.
        if (named.TypeKind == TypeKind.Enum)
        {
            return named.EnumUnderlyingType != null
                && TryComputeLayout(named.EnumUnderlyingType, mode, packOverride: 0, null, depth + 1, visiting, out layout);
        }

        if (named.TypeKind != TypeKind.Struct)
        {
            return false;
        }

        // Self-reference is not expressible in valid C#, but analyzers see invalid source too and the symbol graph still has the cycle.
        visiting ??= new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        if (!visiting.Add(named))
        {
            return false;
        }

        try
        {
            if (!TryReadStructLayout(named, out var isSequential, out var pack, out var declaredSize, out var marshalledCharSize) || !isSequential)
            {
                return false;
            }

            if (packOverride > 0)
            {
                pack = packOverride;
            }

            layout.DeclaredSize = declaredSize;

            // [InlineArray(N)] repeats its single field N times; without this the type measures as one element and every byte count downstream is wrong.
            if (TryGetInlineArrayLength(named, out var inlineLength))
            {
                var element = GetSingleInstanceField(named);
                if (element == null || !TryGetFieldExtent(element, mode, depth + 1, visiting, marshalledCharSize, out var elementSize, out var elementAlign))
                {
                    return false;
                }

                layout.Size = elementSize * inlineLength;
                layout.Alignment = elementAlign;
                layout.LastFieldEnd = layout.Size;
                return true;
            }

            var offset = 0;
            var maxFieldAlignment = 1;
            foreach (var member in named.GetMembers())
            {
                if (member is not IFieldSymbol field || field.IsStatic || field.IsConst)
                {
                    continue;
                }

                if (!TryGetFieldExtent(field, mode, depth + 1, visiting, marshalledCharSize, out var fieldSize, out var fieldAlignment))
                {
                    return false;
                }

                fieldAlignment = fieldAlignment < pack ? fieldAlignment : pack;
                offset = AlignUp(offset, fieldAlignment);
                placements?.Add(new FieldPlacement(field, offset, fieldSize));
                offset += fieldSize;
                maxFieldAlignment = fieldAlignment > maxFieldAlignment ? fieldAlignment : maxFieldAlignment;
            }

            layout.LastFieldEnd = offset;
            layout.Alignment = maxFieldAlignment;

            // The packed probe answers "what would these fields measure if laid out as tightly as the diagnostic recommends", so it must ignore a declared
            // Size as well as the declared Pack — otherwise a struct declared larger than its fields (the shape #816 was found on) would compare equal to
            // itself and never be reported.
            layout.Size = declaredSize > 0 && packOverride == 0 ? declaredSize : AlignUp(offset, maxFieldAlignment);

            // A declared Size below the extent is a TypeLoadException at runtime, not something to report on — but only the MANAGED layout decides that. The
            // marshalled extent of the same struct can legitimately exceed a declared Size, and gating on it there would blind TYPHON011 to the divergence.
            return mode == LayoutMode.Marshalled || layout.Size >= layout.LastFieldEnd;
        }
        finally
        {
            visiting.Remove(named);
        }
    }

    /// <summary>Size and alignment of one field, resolving fixed-size buffers (<c>fixed byte _data[64]</c>) to element size times length.</summary>
    private static bool TryGetFieldExtent(IFieldSymbol field, LayoutMode mode, int depth, HashSet<ISymbol> visiting, int marshalledCharSize,
        out int size, out int alignment)
    {
        size = 0;
        alignment = 1;

        // An explicit [MarshalAs] redefines the field's marshalled width, and modelling every UnmanagedType is not something to guess at: abandon the
        // marshalled pass so TYPHON011 stays quiet rather than accusing a correctly-annotated field.
        if (mode == LayoutMode.Marshalled && HasAttribute(field, MarshalAsAttributeFqn))
        {
            return false;
        }

        if (field.IsFixedSizeBuffer)
        {
            if (field.Type is not IPointerTypeSymbol pointer || !TryGetPrimitiveSize(pointer.PointedAtType, mode, out var elementSize))
            {
                return false;
            }
            size = elementSize * field.FixedSize;
            alignment = elementSize;
            return true;
        }

        // char's marshalled width follows the CONTAINING struct's CharSet, so it is passed down rather than read from the field's own type.
        if (mode == LayoutMode.Marshalled && field.Type.SpecialType == SpecialType.System_Char)
        {
            size = marshalledCharSize;
            alignment = marshalledCharSize;
            return true;
        }

        if (!TryComputeLayout(field.Type, mode, packOverride: 0, null, depth, visiting, out var nested))
        {
            return false;
        }

        size = nested.Size;
        alignment = nested.Alignment;
        return true;
    }

    /// <summary>Reads <c>[StructLayout]</c>. A struct with no attribute is sequential with platform-default packing — what C# emits for every struct.</summary>
    private static bool TryReadStructLayout(INamedTypeSymbol type, out bool isSequential, out int pack, out int declaredSize, out int marshalledCharSize)
    {
        isSequential = true;
        pack = DefaultPack;
        declaredSize = 0;
        marshalledCharSize = 1;   // CharSet.Ansi is the default for a struct, and marshals char to one byte.

        foreach (var attribute in type.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != StructLayoutAttributeFqn)
            {
                continue;
            }

            // LayoutKind: Sequential = 0, Explicit = 2, Auto = 3. Only sequential has a layout this analyzer can predict. The attribute has both an int and a
            // short constructor, and which one binds depends on how the argument was written, so accept either.
            if (attribute.ConstructorArguments.Length == 1)
            {
                var kind = attribute.ConstructorArguments[0].Value;
                if (kind is int intKind)
                {
                    isSequential = intKind == 0;
                }
                else if (kind is short shortKind)
                {
                    isSequential = shortKind == 0;
                }
                else
                {
                    return false;
                }
            }

            foreach (var named in attribute.NamedArguments)
            {
                if (named.Key == "Pack" && named.Value.Value is int declaredPack)
                {
                    pack = declaredPack == 0 ? DefaultPack : declaredPack;
                }
                else if (named.Key == "Size" && named.Value.Value is int size)
                {
                    declaredSize = size;
                }
                else if (named.Key == "CharSet" && named.Value.Value is int charSet)
                {
                    // CharSet: None = 1, Ansi = 2, Unicode = 3, Auto = 4. Unicode marshals char as 2 bytes — the same as managed — and Auto resolves to
                    // Unicode on every platform .NET Core supports.
                    marshalledCharSize = charSet is 3 or 4 ? 2 : 1;
                }
            }
            return true;
        }

        return true;
    }

    private static bool TryGetInlineArrayLength(INamedTypeSymbol type, out int length)
    {
        foreach (var attribute in type.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != InlineArrayAttributeFqn)
            {
                continue;
            }
            if (attribute.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is int declaredLength && declaredLength > 0)
            {
                length = declaredLength;
                return true;
            }
            break;
        }

        length = 0;
        return false;
    }

    private static IFieldSymbol GetSingleInstanceField(INamedTypeSymbol type)
    {
        IFieldSymbol only = null;
        foreach (var member in type.GetMembers())
        {
            if (member is not IFieldSymbol field || field.IsStatic || field.IsConst)
            {
                continue;
            }
            if (only != null)
            {
                return null;
            }
            only = field;
        }
        return only;
    }

    private static bool HasAttribute(ISymbol symbol, string attributeFqn)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() == attributeFqn)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Width of a primitive. <c>bool</c> is the only entry the two tables disagree on here — it marshals to a 4-byte Win32 BOOL. <c>char</c> is handled by the
    /// caller instead, because its marshalled width depends on the containing struct's <c>CharSet</c> rather than on the type.
    /// </summary>
    private static bool TryGetPrimitiveSize(ITypeSymbol type, LayoutMode mode, out int size)
    {
        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean:
                size = mode == LayoutMode.Marshalled ? 4 : 1;
                return true;
            case SpecialType.System_Char:
                size = 2;
                return true;
            case SpecialType.System_SByte:
            case SpecialType.System_Byte:
                size = 1;
                return true;
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
                size = 2;
                return true;
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Single:
                size = 4;
                return true;
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Double:
                size = 8;
                return true;
            default:
                size = 0;
                return false;
        }
    }

    private static int AlignUp(int value, int alignment) => (value + alignment - 1) / alignment * alignment;
}

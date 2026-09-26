using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Typhon.Protocol;

/// <summary>A catalog ready to serve: its canonical form, the bytes <c>WELCOME</c> carries, and their digest, produced together.</summary>
/// <param name="Canonical">The canonical catalog.</param>
/// <param name="Utf8">Its canonical UTF-8 JSON.</param>
/// <param name="Hash">The FNV-1a 64 digest of <paramref name="Utf8"/>.</param>
public sealed record CatalogExport(Catalog Canonical, byte[] Utf8, ulong Hash);

/// <summary>
/// Turns a <see cref="Catalog"/> into the canonical bytes that travel in <c>WELCOME</c>, into the digest a returning client offers back to skip them, and
/// back from bytes into a catalog.
/// </summary>
/// <remarks>
/// <para>
/// <b>Canonical means declaration-order-independent.</b> <see cref="Canonicalize"/> sorts every named collection ordinally and assigns each entry's
/// <c>idx</c> from that order — except built-in commands and metrics, whose indices are reserved and never move, so enabling one cannot renumber the
/// application's entries (W27). Sorting alone would not be enough: <c>idx</c> is the <i>wire</i> index, so if it came from declaration order, reordering two
/// archetypes in registration code would change what the bytes mean. A grid's archetype list is remapped along with the archetypes it names.
/// </para>
/// <para>
/// <b>Field order is wire order (W11).</b> A record body is a concatenation of independently encoded sections — the onEnter section, then one per change
/// group in canonical order — because a state record is assembled per session from group bodies encoded once. Within a section, packed fields come first
/// (they share the section's leading pack, W12), then byte-aligned fields, each by ordinal name. The catalog's field array is exactly that order, so no client
/// ever sorts.
/// </para>
/// <para>
/// <b>The digest is FNV-1a 64 over the canonical bytes</b> (03-wire-protocol § 4). Hashing the bytes rather than folding the structure means nothing that
/// reaches the wire can be left out of the digest by forgetting to fold it, and two catalogs that serialize identically cannot differ in digest. The price is
/// that a change to how the serializer formats a value moves every digest once — one catalog resend per client, which the golden vectors make visible.
/// </para>
/// </remarks>
public static class CatalogSerializer
{
    private static readonly JsonSerializerOptions CanonicalOptions = CatalogJsonContext.Default.Options;

    /// <summary>
    /// Validates <paramref name="catalog"/> and returns an equivalent catalog in canonical form: every named collection sorted, indices assigned, fields in
    /// wire order.
    /// </summary>
    /// <param name="catalog">The catalog as the application declared it.</param>
    /// <returns>A new catalog; the input is not modified, though leaf values (codecs, positions, label arrays) are shared with it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="catalog"/> is <see langword="null"/>.</exception>
    /// <exception cref="CatalogException">The catalog breaks a wire rule.</exception>
    public static Catalog Canonicalize(Catalog catalog)
    {
        CatalogValidator.Validate(catalog);

        var declared = catalog.Archetypes ?? [];
        var archetypes = Sorted(declared, static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        var canonicalIdxOfDeclared = new int[declared.Length];
        for (var i = 0; i < declared.Length; i++)
        {
            canonicalIdxOfDeclared[i] = Array.IndexOf(archetypes, declared[i]);
        }

        for (var i = 0; i < archetypes.Length; i++)
        {
            var a = archetypes[i];
            var groups = SortedStrings(a.Groups);
            archetypes[i] = new CatalogArchetype
            {
                Idx = i,
                Name = a.Name,
                Groups = groups,
                Position = a.Position,
                Fields = SortedArchetypeFields(a.Fields, groups),
                Owner = a.Owner == null ? null : CanonicalOwner(a.Owner),
            };
        }

        var events = CanonicalEvents(catalog.Events);

        var commands = CanonicalCommands(catalog.Commands);
        var metrics = CanonicalMetrics(catalog.Metrics);

        var grids = new CatalogGrid[catalog.Grids?.Length ?? 0];
        for (var i = 0; i < grids.Length; i++)
        {
            var g = catalog.Grids[i];
            var mapped = new int[g.Archetypes?.Length ?? 0];
            for (var j = 0; j < mapped.Length; j++)
            {
                mapped[j] = canonicalIdxOfDeclared[g.Archetypes[j]];
            }

            grids[i] = new CatalogGrid { TileCells = g.TileCells, Archetypes = mapped };
        }

        // Grids have no name to sort by, so they sort by every field that distinguishes them; the validator refuses exact duplicates, so the order is total.
        Array.Sort(grids, CompareGrids);
        for (var i = 0; i < grids.Length; i++)
        {
            var g = grids[i];
            grids[i] = new CatalogGrid { Idx = i, TileCells = g.TileCells, Archetypes = g.Archetypes };
        }

        return new Catalog
        {
            Protocol = catalog.Protocol,
            App = catalog.App,
            Tick = catalog.Tick,
            Limits = catalog.Limits,
            SessionKinds = SortedStrings(catalog.SessionKinds),
            RealmKinds = SortedStrings(catalog.RealmKinds),
            Archetypes = archetypes,
            Enums = SortedEnums(catalog.Enums),
            Events = events,
            Commands = commands,
            Grids = grids,
            Metrics = metrics,
        };
    }

    /// <summary>
    /// Canonicalizes <paramref name="catalog"/> once and returns the canonical form, its bytes and their digest together — what a server serves, computed
    /// in one pass so the bytes and the digest cannot describe two different declarations.
    /// </summary>
    /// <param name="catalog">The catalog as the application declared it.</param>
    /// <returns>The export.</returns>
    public static CatalogExport Export(Catalog catalog)
    {
        var canonical = Canonicalize(catalog);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(canonical, CanonicalOptions);
        return new CatalogExport(canonical, bytes, HashBytes(bytes));
    }

    /// <summary>
    /// Canonicalizes <paramref name="catalog"/> and serializes it to the UTF-8 bytes that travel in <c>WELCOME</c>: no insignificant whitespace, camelCase
    /// names, and defaulted parameters omitted.
    /// </summary>
    /// <param name="catalog">The catalog to serialize.</param>
    /// <returns>The canonical UTF-8 bytes.</returns>
    public static byte[] ToCanonicalUtf8(Catalog catalog) => Export(catalog).Utf8;

    /// <summary>Canonicalizes <paramref name="catalog"/> and returns the digest of its canonical bytes, which a client presents to skip the catalog.</summary>
    /// <param name="catalog">The catalog to digest.</param>
    /// <returns>The digest; on the wire it travels as eight little-endian bytes (W20).</returns>
    public static ulong ComputeHash(Catalog catalog) => Export(catalog).Hash;

    /// <summary>The FNV-1a 64 digest of catalog bytes.</summary>
    /// <param name="utf8">Canonical catalog bytes.</param>
    /// <returns>The digest.</returns>
    public static ulong HashBytes(ReadOnlySpan<byte> utf8)
    {
        var hash = CanonicalHashBuilder.Create();
        hash.AddBytes(utf8);
        return hash.Value;
    }

    /// <summary>
    /// Parses catalog JSON received from a server, validates it, and refuses it unless it is canonical — indices, order and reserved ranges exactly as
    /// <see cref="Canonicalize"/> would produce them. A decode plan built from a non-canonical catalog would silently map mask bits and indices to the
    /// wrong fields, so this is a refusal at <c>WELCOME</c>, never a repair.
    /// </summary>
    /// <param name="utf8Json">The catalog bytes from <c>WELCOME</c>.</param>
    /// <returns>The catalog.</returns>
    /// <exception cref="CatalogException">The JSON is not a catalog, breaks a wire rule, or is not canonical.</exception>
    public static Catalog FromUtf8(ReadOnlySpan<byte> utf8Json)
    {
        Catalog catalog;
        try
        {
            catalog = JsonSerializer.Deserialize(utf8Json, CatalogJsonContext.Default.Catalog);
        }
        catch (JsonException ex)
        {
            throw new CatalogException([$"catalog JSON does not parse: {ex.Message}"]);
        }

        if (catalog == null)
        {
            throw new CatalogException(["catalog JSON is null"]);
        }

        // Validates, then compares the parsed catalog's own serialization with its canonical one. Both go through the model, so a property a newer server
        // adds is ignored on both sides rather than refused.
        var canonical = ToCanonicalUtf8(catalog);
        if (!JsonSerializer.SerializeToUtf8Bytes(catalog, CanonicalOptions).AsSpan().SequenceEqual(canonical))
        {
            throw new CatalogException(["the catalog is not canonical: an index, an order or a reserved range differs from what canonicalization assigns"]);
        }

        return catalog;
    }

    /// <summary>The digest's display form: sixteen lower-case hex digits, most significant first.</summary>
    /// <param name="hash">The digest.</param>
    /// <returns>The hex string.</returns>
    public static string ToHex(ulong hash) => hash.ToString("x16", CultureInfo.InvariantCulture);

    /// <summary>Writes the digest's wire form, eight little-endian bytes, into <paramref name="destination"/>.</summary>
    /// <param name="hash">The digest.</param>
    /// <param name="destination">At least eight bytes.</param>
    public static void WriteHash(ulong hash, Span<byte> destination) => BinaryPrimitives.WriteUInt64LittleEndian(destination, hash);

    /// <summary>Whether a codec kind is packed into its section's leading bit pack rather than byte-aligned (W12).</summary>
    /// <param name="kind">The codec kind.</param>
    /// <returns><see langword="true"/> for <c>bits</c> and <c>bool</c>.</returns>
    public static bool IsPacked(CodecKind kind) => kind is CodecKind.Bits or CodecKind.Bool;

    // Built-ins at their reserved indices, then the application's by name from FirstAppEventIdx (W27), as the commands are.
    private static CatalogEvent[] CanonicalEvents(CatalogEvent[] declared)
    {
        var apps = new List<CatalogEvent>();
        var result = new List<CatalogEvent>();
        foreach (var e in declared ?? [])
        {
            (BuiltInEvents.ReservedIdx(e.Name) >= 0 ? result : apps).Add(e);
        }

        apps.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        for (var i = 0; i < result.Count; i++)
        {
            result[i] = Copy(result[i], BuiltInEvents.ReservedIdx(result[i].Name));
        }

        for (var i = 0; i < apps.Count; i++)
        {
            result.Add(Copy(apps[i], ProtocolConstants.FirstAppEventIdx + i));
        }

        result.Sort(static (a, b) => a.Idx.CompareTo(b.Idx));
        return result.ToArray();

        static CatalogEvent Copy(CatalogEvent e, int idx) => new() { Idx = idx, Name = e.Name, Scope = e.Scope, Fields = SortedMessageFields(e.Fields) };
    }

    private static CatalogCommand[] CanonicalCommands(CatalogCommand[] declared)
    {
        var apps = new List<CatalogCommand>();
        var result = new List<CatalogCommand>();
        foreach (var cmd in declared ?? [])
        {
            (BuiltInCommands.ReservedIdx(cmd.Name) >= 0 ? result : apps).Add(cmd);
        }

        apps.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        for (var i = 0; i < result.Count; i++)
        {
            result[i] = Copy(result[i], BuiltInCommands.ReservedIdx(result[i].Name));
        }

        for (var i = 0; i < apps.Count; i++)
        {
            result.Add(Copy(apps[i], ProtocolConstants.FirstAppCommandIdx + i));
        }

        result.Sort(static (a, b) => a.Idx.CompareTo(b.Idx));
        return result.ToArray();

        static CatalogCommand Copy(CatalogCommand c, int idx) =>
            new() { Idx = idx, Name = c.Name, Delivery = c.Delivery, Rate = c.Rate, Fields = SortedMessageFields(c.Fields) };
    }

    private static CatalogMetric[] CanonicalMetrics(CatalogMetric[] declared)
    {
        var apps = new List<CatalogMetric>();
        var result = new List<CatalogMetric>();
        foreach (var m in declared ?? [])
        {
            var reserved = BuiltInMetrics.ReservedIdx(m.Name);
            if (reserved >= 0)
            {
                result.Add(Copy(m, reserved));
            }
            else
            {
                apps.Add(m);
            }
        }

        apps.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        for (var i = 0; i < apps.Count; i++)
        {
            result.Add(Copy(apps[i], ProtocolConstants.FirstAppMetricIdx + i));
        }

        result.Sort(static (a, b) => a.Idx.CompareTo(b.Idx));
        return result.ToArray();

        static CatalogMetric Copy(CatalogMetric m, int idx) => new()
        {
            Idx = idx,
            Name = m.Name,
            Unit = m.Unit,
            Codec = m.Codec,
            Scope = m.Scope == CatalogMetric.ServerScope ? null : m.Scope,
            Kind = m.Kind == CatalogMetric.GaugeKind ? null : m.Kind,
            Labels = m.Labels,
        };
    }

    private static CatalogOwner CanonicalOwner(CatalogOwner owner)
    {
        var groups = SortedStrings(owner.Groups);
        return new CatalogOwner { Groups = groups, Fields = SortedArchetypeFields(owner.Fields, groups) };
    }

    private static CatalogField[] SortedArchetypeFields(CatalogField[] fields, string[] sortedGroups) =>
        Sorted(fields, (a, b) =>
        {
            var bySection = Section(a, sortedGroups).CompareTo(Section(b, sortedGroups));
            return bySection != 0 ? bySection : CompareWithinSection(a, b);
        });

    private static CatalogField[] SortedMessageFields(CatalogField[] fields) => Sorted(fields, static (a, b) => CompareWithinSection(a, b));

    // Section 0 is the onEnter section; group i is section i + 1.
    private static int Section(CatalogField f, string[] sortedGroups) => f.OnEnter ? 0 : 1 + Array.IndexOf(sortedGroups, f.Group);

    private static int CompareWithinSection(CatalogField a, CatalogField b)
    {
        var byPacking = (IsPacked(a.Codec.Kind) ? 0 : 1).CompareTo(IsPacked(b.Codec.Kind) ? 0 : 1);
        return byPacking != 0 ? byPacking : string.CompareOrdinal(a.Name, b.Name);
    }

    private static int CompareGrids(CatalogGrid a, CatalogGrid b)
    {
        var c = a.TileCells.CompareTo(b.TileCells);
        return c != 0 ? c : CompareSequences(a.Archetypes ?? [], b.Archetypes ?? []);
    }

    private static int CompareSequences<T>(T[] a, T[] b)
        where T : IComparable<T>
    {
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var c = a[i].CompareTo(b[i]);
            if (c != 0)
            {
                return c;
            }
        }

        return a.Length.CompareTo(b.Length);
    }

    private static string[] SortedStrings(string[] values)
    {
        var copy = values == null ? [] : (string[])values.Clone();
        Array.Sort(copy, StringComparer.Ordinal);
        return copy;
    }

    private static T[] Sorted<T>(T[] values, Comparison<T> comparison)
    {
        var copy = values == null ? [] : (T[])values.Clone();
        Array.Sort(copy, comparison);
        return copy;
    }

    // Serialization enumerates a Dictionary in insertion order when nothing was removed, which is what makes this rebuild ordered.
    private static Dictionary<string, string[]> SortedEnums(Dictionary<string, string[]> enums)
    {
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (enums == null)
        {
            return result;
        }

        var keys = new string[enums.Count];
        enums.Keys.CopyTo(keys, 0);
        Array.Sort(keys, StringComparer.Ordinal);
        foreach (var key in keys)
        {
            result[key] = enums[key];
        }

        return result;
    }
}

/// <summary>
/// Source-generated serialization for the catalog. Generated rather than reflection-based so this path stays trim- and AOT-clean (#409), which rules out
/// <c>Reflection.Emit</c>, <c>DynamicMethod</c>, <c>Expression.Compile</c> and <c>Assembly.LoadFrom</c> here.
/// </summary>
/// <remarks>
/// <para>
/// <c>WriteIndented</c> stays false because these bytes are the canonical form the digest and the wire both rest on — insignificant whitespace is not
/// insignificant when it is being hashed and sent to every connecting client.
/// </para>
/// <para>
/// <b>There is deliberately no blanket ignore-when-default.</b> Omitting every defaulted value is right for a codec parameter — a kind that does not read
/// <c>bits</c> should not carry it — and wrong for a required field, where zero is a real value: it dropped <c>"minor":0</c> from the protocol version and
/// <c>"idx":0</c> from the first of everything. Each optional property opts in individually instead.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Catalog))]
internal sealed partial class CatalogJsonContext : JsonSerializerContext;

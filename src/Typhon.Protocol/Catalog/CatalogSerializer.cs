using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Typhon.Protocol;

/// <summary>
/// Turns a <see cref="Catalog"/> into the canonical bytes that travel in <c>WELCOME</c>, and into the digest a returning client offers back to skip them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Canonical means declaration-order-independent.</b> <see cref="Canonicalize"/> sorts every named collection ordinally and then assigns each entry's
/// <c>idx</c> from that order. Sorting alone would not be enough: <c>idx</c> is the <i>wire</i> index, so if it came from declaration order, reordering two
/// archetypes in the application's registration code would change what the bytes mean while leaving the digest free to stay the same. Deriving the index from
/// the sort makes both stable — reordering declarations changes neither the wire nor the hash, while renaming or adding a field changes both.
/// </para>
/// <para>
/// <b>The digest folds the structure, not the JSON text.</b> <see cref="CanonicalHashBuilder"/> exists for exactly this: UTF-8 bytes so the value does not
/// move between processes, fixed-width little-endian integers so it does not move between architectures, and an explicit end-of-entry separator so
/// <c>("Ab", 1)</c> cannot collide with <c>("A", …)</c> by concatenation. Hashing the serialized text instead would make the digest hostage to serializer
/// settings and to how a <c>double</c> happens to render, neither of which is part of the contract.
/// </para>
/// <para>
/// <b>Ordering of the enum table.</b> <see cref="Catalog.Enums"/> is a <see cref="Dictionary{TKey,TValue}"/> and canonicalization rebuilds it by inserting
/// keys in ordinal order. That relies on the framework enumerating a dictionary in insertion order when nothing has been removed — true in practice, and the
/// reason the digest does not depend on it: the fold sorts the keys itself.
/// </para>
/// </remarks>
public static class CatalogSerializer
{
    private static readonly JsonSerializerOptions CanonicalOptions = CatalogJsonContext.Default.Options;

    /// <summary>
    /// Returns an equivalent catalog in canonical form: every named collection ordinally sorted, and every <c>idx</c> assigned from that order.
    /// </summary>
    /// <param name="catalog">The catalog as the application declared it.</param>
    /// <returns>A new catalog; the input is not modified.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="catalog"/> is <see langword="null"/>.</exception>
    public static Catalog Canonicalize(Catalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var archetypes = Sorted(catalog.Archetypes, static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        for (var i = 0; i < archetypes.Length; i++)
        {
            var a = archetypes[i];
            archetypes[i] = new CatalogArchetype
            {
                Idx = i,
                Name = a.Name,
                Groups = SortedStrings(a.Groups),
                Fields = SortedFields(a.Fields),
            };
        }

        var events = Sorted(catalog.Events, static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        for (var i = 0; i < events.Length; i++)
        {
            var e = events[i];
            events[i] = new CatalogEvent { Idx = i, Name = e.Name, Scope = e.Scope, Fields = SortedFields(e.Fields) };
        }

        var commands = Sorted(catalog.Commands, static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        for (var i = 0; i < commands.Length; i++)
        {
            var c = commands[i];
            commands[i] = new CatalogCommand { Idx = i, Name = c.Name, Delivery = c.Delivery, Rate = c.Rate, Fields = SortedFields(c.Fields) };
        }

        var metrics = Sorted(catalog.Metrics, static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        for (var i = 0; i < metrics.Length; i++)
        {
            var m = metrics[i];
            metrics[i] = new CatalogMetric { Idx = i, Name = m.Name, Unit = m.Unit, Codec = m.Codec };
        }

        // Grids have no name to sort by, so they sort by the geometry that distinguishes them: origin first, then cell size.
        var grids = Sorted(catalog.Grids, static (a, b) => CompareGrids(a, b));
        for (var i = 0; i < grids.Length; i++)
        {
            var g = grids[i];
            grids[i] = new CatalogGrid { Idx = i, Origin = g.Origin, Cell = g.Cell, Dims = g.Dims, Archetypes = g.Archetypes };
        }

        return new Catalog
        {
            Protocol = catalog.Protocol,
            App = catalog.App,
            Tick = catalog.Tick,
            Archetypes = archetypes,
            Enums = SortedEnums(catalog.Enums),
            Events = events,
            Commands = commands,
            Grids = grids,
            Metrics = metrics,
        };
    }

    /// <summary>
    /// Canonicalizes <paramref name="catalog"/> and serializes it to the UTF-8 bytes that travel in <c>WELCOME</c>: no insignificant whitespace, camelCase
    /// names, and defaulted parameters omitted.
    /// </summary>
    /// <param name="catalog">The catalog to serialize.</param>
    /// <returns>The canonical UTF-8 bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="catalog"/> is <see langword="null"/>.</exception>
    public static byte[] ToCanonicalUtf8(Catalog catalog) => JsonSerializer.SerializeToUtf8Bytes(Canonicalize(catalog), CanonicalOptions);

    /// <summary>
    /// Canonicalizes <paramref name="catalog"/> and returns its 64-bit FNV-1a digest as sixteen lower-case hex digits — the token a client presents to
    /// skip the catalog on a later connection.
    /// </summary>
    /// <param name="catalog">The catalog to digest.</param>
    /// <returns>Sixteen hexadecimal characters.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="catalog"/> is <see langword="null"/>.</exception>
    public static string ComputeHash(Catalog catalog)
    {
        var c = Canonicalize(catalog);
        var hash = CanonicalHashBuilder.Create();

        if (c.Protocol != null)
        {
            hash.AddInt32(c.Protocol.Major);
            hash.AddInt32(c.Protocol.Minor);
        }

        hash.EndEntry();

        if (c.App != null)
        {
            hash.AddUtf8(c.App.Name);
            hash.AddInt32(c.App.Revision);
        }

        hash.EndEntry();

        if (c.Tick != null)
        {
            hash.AddInt32(c.Tick.PeriodUs);
            hash.AddInt32(c.Tick.PingHz);
        }

        hash.EndEntry();

        foreach (var a in c.Archetypes)
        {
            hash.AddInt32(a.Idx);
            hash.AddUtf8(a.Name);
            foreach (var g in a.Groups)
            {
                hash.AddUtf8(g);
            }

            hash.EndEntry();
            FoldFields(ref hash, a.Fields);
        }

        foreach (var pair in SortedKeys(c.Enums))
        {
            hash.AddUtf8(pair);
            foreach (var name in c.Enums[pair])
            {
                hash.AddUtf8(name);
            }

            hash.EndEntry();
        }

        foreach (var e in c.Events)
        {
            hash.AddInt32(e.Idx);
            hash.AddUtf8(e.Name);
            hash.AddUtf8(e.Scope);
            hash.EndEntry();
            FoldFields(ref hash, e.Fields);
        }

        foreach (var cmd in c.Commands)
        {
            hash.AddInt32(cmd.Idx);
            hash.AddUtf8(cmd.Name);
            hash.AddUtf8(cmd.Delivery);
            if (cmd.Rate != null)
            {
                hash.AddInt32(cmd.Rate.PerSec);
                hash.AddInt32(cmd.Rate.Burst);
            }

            hash.EndEntry();
            FoldFields(ref hash, cmd.Fields);
        }

        foreach (var g in c.Grids)
        {
            hash.AddInt32(g.Idx);
            FoldDoubles(ref hash, g.Origin);
            FoldDouble(ref hash, g.Cell);
            FoldInts(ref hash, g.Dims);
            FoldInts(ref hash, g.Archetypes);
            hash.EndEntry();
        }

        foreach (var m in c.Metrics)
        {
            hash.AddInt32(m.Idx);
            hash.AddUtf8(m.Name);
            hash.AddUtf8(m.Unit);
            FoldCodec(ref hash, m.Codec);
            hash.EndEntry();
        }

        return hash.ToHex();
    }

    private static void FoldFields(ref CanonicalHashBuilder hash, CatalogField[] fields)
    {
        if (fields == null)
        {
            return;
        }

        foreach (var f in fields)
        {
            hash.AddUtf8(f.Name);
            hash.AddUtf8(f.Group);
            hash.AddUtf8(f.Enum);
            hash.AddUtf8(f.Smoothing);
            FoldCodec(ref hash, f.Codec);
            if (f.Motion != null)
            {
                hash.AddUtf8(f.Motion.Velocity);
                hash.AddUtf8(f.Motion.Model);
                hash.AddUtf8(f.Motion.Discontinuity);
                FoldDouble(ref hash, f.Motion.Tolerance);
            }

            hash.EndEntry();
        }
    }

    private static void FoldCodec(ref CanonicalHashBuilder hash, CatalogCodec codec)
    {
        if (codec == null)
        {
            return;
        }

        hash.AddInt32((int)codec.Kind);
        hash.AddInt32(codec.Bits);
        hash.AddInt32(codec.QuantaDiv);
        hash.AddInt32(codec.N);
        hash.AddInt32(codec.MaxBytes);
        hash.AddInt32(codec.FixedBytes);
        hash.AddUtf8(codec.Pack);
        FoldDouble(ref hash, codec.Scale);
        FoldDoubles(ref hash, codec.Min);
        FoldDoubles(ref hash, codec.Max);
    }

    // Folded as raw IEEE bits rather than a rendered string: a double's text form depends on the formatter, and the digest must not.
    private static void FoldDouble(ref CanonicalHashBuilder hash, double value) => hash.AddInt64(BitConverter.DoubleToInt64Bits(value));

    private static void FoldDoubles(ref CanonicalHashBuilder hash, double[] values)
    {
        if (values == null)
        {
            return;
        }

        foreach (var v in values)
        {
            FoldDouble(ref hash, v);
        }
    }

    private static void FoldInts(ref CanonicalHashBuilder hash, int[] values)
    {
        if (values == null)
        {
            return;
        }

        foreach (var v in values)
        {
            hash.AddInt32(v);
        }
    }

    private static int CompareGrids(CatalogGrid a, CatalogGrid b)
    {
        var ao = a.Origin ?? [];
        var bo = b.Origin ?? [];
        for (var i = 0; i < Math.Min(ao.Length, bo.Length); i++)
        {
            var c = ao[i].CompareTo(bo[i]);
            if (c != 0)
            {
                return c;
            }
        }

        var byLength = ao.Length.CompareTo(bo.Length);
        return byLength != 0 ? byLength : a.Cell.CompareTo(b.Cell);
    }

    private static CatalogField[] SortedFields(CatalogField[] fields) => Sorted(fields, static (a, b) => string.CompareOrdinal(a.Name, b.Name));

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

    private static Dictionary<string, string[]> SortedEnums(Dictionary<string, string[]> enums)
    {
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (enums == null)
        {
            return result;
        }

        foreach (var key in SortedKeys(enums))
        {
            result[key] = enums[key];
        }

        return result;
    }

    private static string[] SortedKeys(Dictionary<string, string[]> enums)
    {
        if (enums == null)
        {
            return [];
        }

        var keys = new string[enums.Count];
        enums.Keys.CopyTo(keys, 0);
        Array.Sort(keys, StringComparer.Ordinal);
        return keys;
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

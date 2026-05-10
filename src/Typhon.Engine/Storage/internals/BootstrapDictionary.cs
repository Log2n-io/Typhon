using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using JetBrains.Annotations;

namespace Typhon.Engine.Internals;

/// <summary>
/// Bootstrap dictionary stored as a compact byte stream on a single page.
/// Provides startup configuration, segment pointers, and engine metadata.
/// </summary>
/// <remarks>
/// <para>On-disk format:</para>
/// <code>
/// [StreamLength:2B]  // total bytes of entries (excluding this header and 0xFF sentinel)
/// [TypeTag:1B] [Key:UTF8+NUL] [Value:N bytes] ...
/// [0xFF]             // end sentinel
/// </code>
/// <para>In-memory: <c>Dictionary&lt;string, BootstrapValue&gt;</c> for O(1) lookup.</para>
/// </remarks>
[PublicAPI]
public class BootstrapDictionary
{
    /// <summary>
    /// Type tag for bootstrap dictionary values. Determines the on-disk byte size and in-memory interpretation.
    /// </summary>
    [PublicAPI]
    public enum ValueType : byte
    {
        Bool     = 0x01,  // 1 byte
        Int1     = 0x02,  // 4 bytes (1 × int)
        Int2     = 0x03,  // 8 bytes (2 × int)
        Int3     = 0x04,  // 12 bytes (3 × int)
        Int4     = 0x05,  // 16 bytes (4 × int)
        Int5     = 0x06,  // 20 bytes (5 × int)
        Int6     = 0x07,  // 24 bytes (6 × int)
        Long     = 0x08,  // 8 bytes
        DateTime = 0x09,  // 8 bytes (DateTime.Ticks as long)
        String   = 0x0A,  // NUL-terminated UTF8

        End      = 0xFF,  // sentinel — end of stream
    }

    /// <summary>
    /// A dynamically-typed value stored in the bootstrap dictionary.
    /// Wraps a small array of ints (1-6), a long, a bool, a DateTime, or a string.
    /// </summary>
    [PublicAPI]
    public readonly struct Value
    {
        public readonly ValueType Type;
        private readonly long _scalar;      // for Long, DateTime, Bool
        private readonly int[] _ints;       // for Int1..Int6 (null for scalar types)
        private readonly string _string;    // for String type

        private Value(ValueType type, long scalar, int[] ints = null, string str = null)
        {
            Type = type;
            _scalar = scalar;
            _ints = ints;
            _string = str;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Factory methods
        // ═══════════════════════════════════════════════════════════════════════

        public static Value FromBool(bool value) => new(ValueType.Bool, value ? 1 : 0);
        public static Value FromInt(int v0) => new(ValueType.Int1, 0, [v0]);
        public static Value FromInt2(int v0, int v1) => new(ValueType.Int2, 0, [v0, v1]);
        public static Value FromInt3(int v0, int v1, int v2) => new(ValueType.Int3, 0, [v0, v1, v2]);
        public static Value FromInt4(int v0, int v1, int v2, int v3) => new(ValueType.Int4, 0, [v0, v1, v2, v3]);
        public static Value FromInt5(int v0, int v1, int v2, int v3, int v4) => new(ValueType.Int5, 0, [v0, v1, v2, v3, v4]);
        public static Value FromInt6(int v0, int v1, int v2, int v3, int v4, int v5) => new(ValueType.Int6, 0, [v0, v1, v2, v3, v4, v5]);
        public static Value FromLong(long value) => new(ValueType.Long, value);
        public static Value FromDateTime(DateTime value) => new(ValueType.DateTime, value.Ticks);
        public static Value FromString(string value) => new(ValueType.String, 0, str: value);

        // ═══════════════════════════════════════════════════════════════════════
        // Accessors
        // ═══════════════════════════════════════════════════════════════════════

        public bool AsBool
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                Debug.Assert(Type == ValueType.Bool);
                return _scalar != 0;
            }
        }

        public long AsLong
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                Debug.Assert(Type == ValueType.Long);
                return _scalar;
            }
        }

        public DateTime AsDateTime
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                Debug.Assert(Type == ValueType.DateTime);
                return new DateTime(_scalar);
            }
        }

        public string AsString
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                Debug.Assert(Type == ValueType.String);
                return _string;
            }
        }

        /// <summary>Get the int value at the given index (0-based). Valid for Int1..Int6.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetInt(int index = 0)
        {
            Debug.Assert(_ints != null && index >= 0 && index < _ints.Length);
            return _ints[index];
        }

        /// <summary>Number of int values (1-6 for IntN types, 0 for others).</summary>
        public int IntCount => _ints?.Length ?? 0;

        /// <summary>Shorthand: first int value. Valid for Int1..Int6.</summary>
        public int AsInt => GetInt(0);

        public override string ToString() => Type switch
        {
            ValueType.Bool => $"Bool({AsBool})",
            ValueType.Long => $"Long({_scalar})",
            ValueType.DateTime => $"DateTime({AsDateTime:O})",
            ValueType.String => $"String({_string})",
            >= ValueType.Int1 and <= ValueType.Int6 => $"Int{_ints.Length}({string.Join(", ", _ints)})",
            _ => $"Unknown({Type})"
        };
    }

    private readonly Dictionary<string, Value> _entries = new();

    /// <summary>Number of entries in the dictionary.</summary>
    public int Count => _entries.Count;

    /// <summary>All keys in the dictionary.</summary>
    public IEnumerable<string> Keys => _entries.Keys;

    // ═══════════════════════════════════════════════════════════════════════
    // Read/Write API
    // ═══════════════════════════════════════════════════════════════════════

    public void Set(string key, Value value) => _entries[key] = value;

    public bool TryGet(string key, out Value value) => _entries.TryGetValue(key, out value);

    public Value Get(string key) => _entries.TryGetValue(key, out var value) ? value : throw new KeyNotFoundException($"Bootstrap key '{key}' not found");

    public bool ContainsKey(string key) => _entries.ContainsKey(key);

    public void Remove(string key) => _entries.Remove(key);

    public void Clear() => _entries.Clear();

    // ═══════════════════════════════════════════════════════════════════════
    // Convenience setters
    // ═══════════════════════════════════════════════════════════════════════

    public void SetBool(string key, bool value) => _entries[key] = Value.FromBool(value);
    public void SetInt(string key, int value) => _entries[key] = Value.FromInt(value);
    public void SetLong(string key, long value) => _entries[key] = Value.FromLong(value);
    public void SetDateTime(string key, DateTime value) => _entries[key] = Value.FromDateTime(value);
    public void SetString(string key, string value) => _entries[key] = Value.FromString(value);

    // ═══════════════════════════════════════════════════════════════════════
    // Convenience getters (with defaults)
    // ═══════════════════════════════════════════════════════════════════════

    public int GetInt(string key, int defaultValue = 0) => _entries.TryGetValue(key, out var v) ? v.AsInt : defaultValue;

    public long GetLong(string key, long defaultValue = 0) => _entries.TryGetValue(key, out var v) ? v.AsLong : defaultValue;

    public bool GetBool(string key, bool defaultValue = false) => _entries.TryGetValue(key, out var v) ? v.AsBool : defaultValue;

    // ═══════════════════════════════════════════════════════════════════════
    // Serialization — write to byte stream
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Serialize the dictionary to a byte stream. Format:
    /// [StreamLength:2B] [entries...] [0xFF]
    /// </summary>
    public unsafe int WriteTo(byte* dest, int maxBytes)
    {
        byte* start = dest;
        byte* limit = dest + maxBytes - 1;  // reserve 1 byte for 0xFF sentinel
        dest += 2;                          // skip length header, filled at the end

        foreach (var kvp in _entries)
        {
            byte* entryStart = dest;

            // Type tag
            if (dest >= limit)
            {
                ThrowStreamOverflow();
            }
            *dest++ = (byte)kvp.Value.Type;

            // Key: UTF8 + NUL
            int keyBytes = Encoding.UTF8.GetByteCount(kvp.Key);
            if (dest + keyBytes + 1 >= limit)
            {
                ThrowStreamOverflow();
            }
            fixed (char* keyChars = kvp.Key)
            {
                Encoding.UTF8.GetBytes(keyChars, kvp.Key.Length, dest, keyBytes);
            }
            dest += keyBytes;
            *dest++ = 0; // NUL terminator

            // Value data
            int valueSize = GetValueSize(kvp.Value);
            if (dest + valueSize >= limit)
            {
                ThrowStreamOverflow();
            }
            WriteValue(dest, kvp.Value);
            dest += valueSize;
        }

        // End sentinel
        *dest++ = (byte)ValueType.End;

        // Write stream length at the start (entry bytes only, excluding 2B header and 1B sentinel)
        int totalEntryBytes = (int)(dest - start - 2 - 1);
        *(ushort*)start = (ushort)totalEntryBytes;

        return (int)(dest - start);
    }

    /// <summary>Calculate the total serialized size in bytes.</summary>
    public int CalculateSize()
    {
        int size = 2 + 1; // 2B header + 1B sentinel
        foreach (var kvp in _entries)
        {
            size += 1; // type tag
            size += Encoding.UTF8.GetByteCount(kvp.Key) + 1; // key + NUL
            size += GetValueSize(kvp.Value);
        }
        return size;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Deserialization — read from byte stream
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Deserialize the dictionary from a byte stream. Clears existing entries.
    /// </summary>
    public unsafe void ReadFrom(byte* src, int maxBytes)
    {
        _entries.Clear();

        byte* start = src;
        byte* limit = src + maxBytes;

        // Read stream length
        if (src + 2 >= limit)
        {
            return;
        }
        ushort streamLength = *(ushort*)src;
        src += 2;

        byte* streamEnd = src + streamLength;
        if (streamEnd > limit)
        {
            streamEnd = limit;
        }

        while (src < streamEnd)
        {
            // Type tag
            var typeTag = (ValueType)(*src++);
            if (typeTag == ValueType.End)
            {
                break;
            }

            // Key: scan for NUL
            byte* keyStart = src;
            while (src < streamEnd && *src != 0)
            {
                src++;
            }
            if (src >= streamEnd)
            {
                break; // truncated stream
            }
            int keyLen = (int)(src - keyStart);
            string key = Encoding.UTF8.GetString(keyStart, keyLen);
            src++; // skip NUL

            // Value
            var value = ReadValue(ref src, streamEnd, typeTag);
            _entries[key] = value;
        }

        // Verify sentinel
        if (src < limit)
        {
            Debug.Assert(*src == (byte)ValueType.End, "Bootstrap stream missing end sentinel");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Private helpers
    // ═══════════════════════════════════════════════════════════════════════

    private static int GetValueSize(Value value) => value.Type switch
    {
        ValueType.Bool => 1,
        ValueType.Int1 => 4,
        ValueType.Int2 => 8,
        ValueType.Int3 => 12,
        ValueType.Int4 => 16,
        ValueType.Int5 => 20,
        ValueType.Int6 => 24,
        ValueType.Long => 8,
        ValueType.DateTime => 8,
        ValueType.String => Encoding.UTF8.GetByteCount(value.AsString) + 1, // + NUL
        _ => 0
    };

    private static unsafe void WriteValue(byte* dest, Value value)
    {
        switch (value.Type)
        {
            case ValueType.Bool:
                *dest = value.AsBool ? (byte)1 : (byte)0;
                break;

            case >= ValueType.Int1 and <= ValueType.Int6:
                for (int i = 0; i < value.IntCount; i++)
                {
                    *(int*)(dest + i * 4) = value.GetInt(i);
                }
                break;

            case ValueType.Long:
                *(long*)dest = value.AsLong;
                break;

            case ValueType.DateTime:
                *(long*)dest = value.AsDateTime.Ticks;
                break;

            case ValueType.String:
                var str = value.AsString;
                int len = Encoding.UTF8.GetByteCount(str);
                fixed (char* chars = str)
                {
                    Encoding.UTF8.GetBytes(chars, str.Length, dest, len);
                }
                dest[len] = 0; // NUL
                break;
        }
    }

    private static unsafe Value ReadValue(ref byte* src, byte* limit, ValueType type)
    {
        switch (type)
        {
            case ValueType.Bool:
                if (src >= limit) { return default; }
                bool b = *src++ != 0;
                return Value.FromBool(b);

            case >= ValueType.Int1 and <= ValueType.Int6:
                int count = (byte)type - (byte)ValueType.Int1 + 1;
                if (src + count * 4 > limit) { return default; }
                var ints = new int[count];
                for (int i = 0; i < count; i++)
                {
                    ints[i] = *(int*)(src + i * 4);
                }
                src += count * 4;
                return count switch
                {
                    1 => Value.FromInt(ints[0]),
                    2 => Value.FromInt2(ints[0], ints[1]),
                    3 => Value.FromInt3(ints[0], ints[1], ints[2]),
                    4 => Value.FromInt4(ints[0], ints[1], ints[2], ints[3]),
                    5 => Value.FromInt5(ints[0], ints[1], ints[2], ints[3], ints[4]),
                    6 => Value.FromInt6(ints[0], ints[1], ints[2], ints[3], ints[4], ints[5]),
                    _ => default
                };

            case ValueType.Long:
                if (src + 8 > limit) { return default; }
                long l = *(long*)src;
                src += 8;
                return Value.FromLong(l);

            case ValueType.DateTime:
                if (src + 8 > limit) { return default; }
                long ticks = *(long*)src;
                src += 8;
                return Value.FromDateTime(new DateTime(ticks));

            case ValueType.String:
                byte* strStart = src;
                while (src < limit && *src != 0) { src++; }
                if (src >= limit) { return default; }
                int strLen = (int)(src - strStart);
                string s = Encoding.UTF8.GetString(strStart, strLen);
                src++; // skip NUL
                return Value.FromString(s);

            default:
                return default;
        }
    }

    private static void ThrowStreamOverflow() => throw new InvalidOperationException("Bootstrap dictionary stream exceeds available page space");
}
